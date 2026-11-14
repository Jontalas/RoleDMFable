using System.Globalization;
using System.Text;
using System.Text.Json;

namespace RoleDMFable.Core;

/// <summary>Una página de texto tal como sale del archivo. Sin número si el formato no tiene páginas.</summary>
public sealed record RulePage(int? Number, string Text);

/// <summary>Un trozo de reglamento del tamaño de un párrafo largo: la unidad que se busca y se cita.</summary>
public sealed record RulePassage(int? Page, string Text, int? LastPage = null)
{
    /// <summary>«pág. 12», «págs. 12-13» o vacío si el formato no tiene páginas.</summary>
    public string PageLabel => Page is not { } first ? ""
        : LastPage is { } last && last != first ? $"págs. {first}-{last}"
        : $"pág. {first}";
}

/// <summary>Un pasaje encontrado, con el libro del que sale y lo bien que encaja.</summary>
public sealed record RuleHit(string Book, RulePassage Passage, double Score)
{
    public string Text => Passage.Text;
    public int? Page => Passage.Page;

    /// <summary>De dónde sale, listo para citar: «Manual básico, pág. 12».</summary>
    public string Source => Passage.PageLabel.Length > 0 ? $"{Book}, {Passage.PageLabel}" : Book;
}

/// <summary>Un reglamento ya leído y troceado en pasajes.</summary>
public sealed class Rulebook
{
    public const int SaveVersion = 1;

    /// <summary>Tamaño objetivo de un pasaje, en palabras.</summary>
    public const int PassageWords = 180;

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string Id { get; }
    public string Title { get; }
    public string FileName { get; }
    public IReadOnlyList<RulePassage> Passages { get; }
    public bool HasPages => Passages.Any(p => p.Page is not null);

    public Rulebook(string id, string title, string fileName, IReadOnlyList<RulePassage> passages)
    {
        Id = id;
        Title = title;
        FileName = fileName;
        Passages = passages;
    }

    /// <summary>Trocea las páginas en pasajes. Lanza <see cref="FormatException"/> si no hay texto.</summary>
    public static Rulebook FromPages(string title, string fileName, IEnumerable<RulePage> pages)
    {
        var passages = new List<RulePassage>();
        var words = new List<string>();
        int? startPage = null;
        int? lastPage = null;

        void Flush()
        {
            if (words.Count == 0) return;
            passages.Add(new RulePassage(startPage, string.Join(' ', words), lastPage == startPage ? null : lastPage));
            words.Clear();
        }

        foreach (var page in pages)
        {
            foreach (var paragraph in SplitParagraphs(page.Text))
            {
                var paragraphWords = paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                // Un párrafo no se parte salvo que él solo ya sea más largo que un pasaje.
                if (words.Count > 0 && words.Count + paragraphWords.Length > PassageWords) Flush();
                foreach (var word in paragraphWords)
                {
                    if (words.Count == 0) startPage = page.Number;
                    lastPage = page.Number;
                    words.Add(word);
                    if (words.Count >= PassageWords * 3 / 2) Flush();
                }
            }
        }
        Flush();

        if (passages.Count == 0) throw new FormatException("El archivo no contiene texto que se pueda leer.");
        // El id sale del nombre del archivo: volver a cargar el mismo archivo lo sustituye.
        return new Rulebook(MakeId(fileName.Length > 0 ? fileName : title), title, fileName, passages);
    }

    public string ToJson() => JsonSerializer.Serialize(new Save(SaveVersion, Id, Title, FileName, Passages), Json);

    public static Rulebook FromJson(string json)
    {
        Save? save;
        try
        {
            save = JsonSerializer.Deserialize<Save>(json, Json);
        }
        catch (JsonException e)
        {
            throw new FormatException("El reglamento guardado está dañado.", e);
        }
        if (save is null || save.Version != SaveVersion || string.IsNullOrEmpty(save.Id) || save.Passages is null || save.Passages.Count == 0)
            throw new FormatException("El reglamento guardado está dañado o es de otra versión.");
        return new Rulebook(save.Id, save.Title ?? save.Id, save.FileName ?? "", save.Passages);
    }

    private sealed record Save(int Version, string Id, string? Title, string? FileName, IReadOnlyList<RulePassage>? Passages);

    private static IEnumerable<string> SplitParagraphs(string text)
    {
        var current = new StringBuilder();
        foreach (var raw in text.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                if (current.Length > 0) yield return current.ToString();
                current.Clear();
                continue;
            }
            // Palabra partida con guion al final de línea: se vuelve a unir.
            if (current.Length > 1 && current[^1] == '-' && char.IsLetter(current[^2]) && char.IsLower(line[0])) current.Length--;
            else if (current.Length > 0) current.Append(' ');
            current.Append(line);
        }
        if (current.Length > 0) yield return current.ToString();
    }

    private static string MakeId(string title)
    {
        var id = new string(RulesText.Fold(title).Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Trim('_');
        while (id.Contains("__")) id = id.Replace("__", "_");
        return id.Length > 0 ? id : "reglamento";
    }
}

/// <summary>Los reglamentos cargados y su buscador (BM25, sin conexión y sin IA).</summary>
public sealed class RulesLibrary
{
    private const double K1 = 1.4;
    private const double B = 0.75;

    private readonly List<Rulebook> _books = new();
    private List<Entry> _entries = new();
    private Dictionary<string, int> _documentFrequency = new();
    private double _averageLength = 1;

    public IReadOnlyList<Rulebook> Books => _books;

    public event Action? Changed;

    /// <summary>Añade un reglamento. Si ya había uno con el mismo id, lo sustituye.</summary>
    public void Add(Rulebook book)
    {
        _books.RemoveAll(b => b.Id == book.Id);
        _books.Add(book);
        Rebuild();
    }

    public bool Remove(string id)
    {
        if (_books.RemoveAll(b => b.Id == id) == 0) return false;
        Rebuild();
        return true;
    }

    public IReadOnlyList<RuleHit> Search(string query, int max = 4)
    {
        var terms = RulesText.Terms(query).Distinct().ToList();
        if (terms.Count == 0 || _entries.Count == 0) return Array.Empty<RuleHit>();

        var hits = new List<RuleHit>();
        foreach (var entry in _entries)
        {
            double score = 0;
            foreach (var term in terms)
            {
                if (!entry.Counts.TryGetValue(term, out var count)) continue;
                var frequency = _documentFrequency[term];
                var idf = Math.Log(1 + (_entries.Count - frequency + 0.5) / (frequency + 0.5));
                score += idf * count * (K1 + 1) / (count + K1 * (1 - B + B * entry.Length / _averageLength));
            }
            if (score > 0) hits.Add(new RuleHit(entry.Book.Title, entry.Passage, score));
        }
        return hits.OrderByDescending(h => h.Score).Take(max).ToList();
    }

    private void Rebuild()
    {
        var entries = new List<Entry>();
        var documentFrequency = new Dictionary<string, int>();
        long total = 0;
        foreach (var book in _books)
        {
            foreach (var passage in book.Passages)
            {
                var counts = new Dictionary<string, int>();
                var length = 0;
                foreach (var term in RulesText.Terms(passage.Text))
                {
                    counts[term] = counts.GetValueOrDefault(term) + 1;
                    length++;
                }
                foreach (var term in counts.Keys) documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
                entries.Add(new Entry(book, passage, counts, length));
                total += length;
            }
        }
        _entries = entries;
        _documentFrequency = documentFrequency;
        _averageLength = entries.Count > 0 ? Math.Max(1.0, (double)total / entries.Count) : 1;
        Changed?.Invoke();
    }

    private sealed record Entry(Rulebook Book, RulePassage Passage, Dictionary<string, int> Counts, int Length);
}

/// <summary>Cómo se convierten las palabras en términos de búsqueda: sin tildes, sin plurales y sin palabras vacías.</summary>
public static class RulesText
{
    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "de", "la", "el", "en", "que", "los", "las", "un", "una", "uno", "unos", "unas", "del", "al", "se", "por", "con", "para",
        "su", "sus", "es", "son", "lo", "le", "les", "como", "mas", "pero", "si", "no", "ya", "o", "u", "y", "e", "a", "ni",
        "este", "esta", "esto", "estos", "estas", "ese", "esa", "eso", "ser", "hay", "ha", "han", "me", "te", "mi", "tu",
        "cuando", "cual", "cuales", "donde", "que", "quien", "sobre", "entre", "hasta", "desde", "cada", "tambien", "muy",
        "the", "of", "and", "to", "in", "is", "it", "for", "on", "with", "as", "by", "an", "be", "or", "at",
    };

    /// <summary>Minúsculas y sin tildes ni diéresis (la ñ se conserva).</summary>
    public static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.ToLowerInvariant().Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) builder.Append(c);
            else if (c == '̃' && builder.Length > 0 && builder[^1] == 'n') builder[^1] = 'ñ';
        }
        return builder.ToString();
    }

    public static IEnumerable<string> Terms(string text)
    {
        var folded = Fold(text);
        var start = -1;
        for (var i = 0; i <= folded.Length; i++)
        {
            var inside = i < folded.Length && char.IsLetterOrDigit(folded[i]);
            if (inside && start < 0) start = i;
            if (inside || start < 0) continue;
            var word = folded[start..i];
            start = -1;
            if (word.Length < 2 || StopWords.Contains(word)) continue;
            yield return Stem(word);
        }
    }

    /// <summary>Recorte muy ligero de plurales, para que «ataques» encuentre «ataque».</summary>
    private static string Stem(string word)
    {
        if (word.Length > 5 && word.EndsWith("es", StringComparison.Ordinal) && !IsVowel(word[^3])) return word[..^2];
        if (word.Length > 4 && word[^1] == 's') return word[..^1];
        return word;
    }

    private static bool IsVowel(char c) => c is 'a' or 'e' or 'i' or 'o' or 'u';
}
