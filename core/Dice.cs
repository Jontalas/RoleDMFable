using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RoleDMFable.Core;

/// <summary>Fuente de azar. El anfitrión es el único que tira; los tests usan una fija.</summary>
public interface IRandomSource
{
    /// <summary>Entero entre 1 y <paramref name="sides"/>, ambos incluidos.</summary>
    int Next(int sides);
}

public sealed class SystemRandomSource : IRandomSource
{
    private readonly Random _random;
    public SystemRandomSource(int? seed = null) => _random = seed is null ? new Random() : new Random(seed.Value);
    public int Next(int sides) => _random.Next(1, sides + 1);
}

/// <summary>Un sumando de la expresión: un grupo de dados (2d6, 4d6kh3) o una constante.</summary>
public sealed record DiceTerm(int Sign, int Count, int Sides, int Keep, bool KeepHighest)
{
    public bool IsConstant => Sides == 0;
}

public sealed record DiceTermResult(DiceTerm Term, IReadOnlyList<int> Rolls, IReadOnlyList<bool> Kept, int Subtotal);

public sealed record DiceResult(string Expression, IReadOnlyList<DiceTermResult> Terms, int Total)
{
    /// <summary>Texto para el registro: «2d6+3 → [4, 2] + 3 = 9». Los dados descartados van entre paréntesis.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < Terms.Count; i++)
        {
            var t = Terms[i];
            if (i > 0) sb.Append(t.Term.Sign < 0 ? " - " : " + ");
            else if (t.Term.Sign < 0) sb.Append('-');
            if (t.Term.IsConstant)
            {
                sb.Append(t.Term.Count.ToString(CultureInfo.InvariantCulture));
                continue;
            }
            sb.Append('[');
            for (var d = 0; d < t.Rolls.Count; d++)
            {
                if (d > 0) sb.Append(", ");
                sb.Append(t.Kept[d] ? t.Rolls[d].ToString(CultureInfo.InvariantCulture) : $"({t.Rolls[d]})");
            }
            sb.Append(']');
        }
        return $"{Expression} → {sb} = {Total}";
    }
}

/// <summary>
/// Notación de dados: sumas y restas de NdM y constantes, con kh/kl para quedarse
/// con los más altos o más bajos. Ejemplos: d20, 2d6+3, 4d6kh3, 1d20+5-1d4.
/// </summary>
public sealed class DiceExpression
{
    public const int MaxDice = 100;
    public const int MaxSides = 1000;

    private static readonly Regex TermPattern = new(
        @"\G(?<sign>[+-])?(?:(?<count>\d{0,3})d(?<sides>\d{1,4})(?:(?<mode>kh|kl)(?<keep>\d{1,3}))?|(?<const>\d{1,6}))",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Text { get; }
    public IReadOnlyList<DiceTerm> Terms { get; }

    private DiceExpression(string text, IReadOnlyList<DiceTerm> terms)
    {
        Text = text;
        Terms = terms;
    }

    public static DiceExpression Parse(string input) =>
        TryParse(input, out var expression, out var error) ? expression! : throw new FormatException(error);

    public static bool TryParse(string? input, out DiceExpression? expression, out string error)
    {
        expression = null;
        error = "";
        // Un espacio entre dos cifras o letras («2d6 3») es ambiguo: no lo pegamos en «2d63».
        if (Regex.IsMatch(input ?? "", @"[0-9a-zA-Z]\s+[0-9a-zA-Z]"))
        {
            error = "Falta un + o un - entre los sumandos.";
            return false;
        }
        var text = new string((input ?? "").Where(c => !char.IsWhiteSpace(c)).ToArray()).ToLowerInvariant();
        if (text.Length == 0)
        {
            error = "Escribe una tirada, por ejemplo 2d6+3.";
            return false;
        }

        var terms = new List<DiceTerm>();
        var position = 0;
        while (position < text.Length)
        {
            var match = TermPattern.Match(text, position);
            if (!match.Success || match.Length == 0 || (position > 0 && !match.Groups["sign"].Success))
            {
                error = $"No entiendo «{text[position..]}». Usa algo como 2d6+3 o 4d6kh3.";
                return false;
            }
            var sign = match.Groups["sign"].Value == "-" ? -1 : 1;
            if (match.Groups["const"].Success)
            {
                terms.Add(new DiceTerm(sign, int.Parse(match.Groups["const"].Value, CultureInfo.InvariantCulture), 0, 0, true));
            }
            else
            {
                var countText = match.Groups["count"].Value;
                var count = countText.Length == 0 ? 1 : int.Parse(countText, CultureInfo.InvariantCulture);
                var sides = int.Parse(match.Groups["sides"].Value, CultureInfo.InvariantCulture);
                if (count < 1 || count > MaxDice)
                {
                    error = $"El número de dados debe estar entre 1 y {MaxDice}.";
                    return false;
                }
                if (sides < 2 || sides > MaxSides)
                {
                    error = $"Las caras del dado deben estar entre 2 y {MaxSides}.";
                    return false;
                }
                var keep = count;
                var keepHighest = true;
                if (match.Groups["mode"].Success)
                {
                    keep = int.Parse(match.Groups["keep"].Value, CultureInfo.InvariantCulture);
                    keepHighest = match.Groups["mode"].Value == "kh";
                    if (keep < 1 || keep > count)
                    {
                        error = $"No puedes quedarte con {keep} dados de {count}.";
                        return false;
                    }
                }
                terms.Add(new DiceTerm(sign, count, sides, keep, keepHighest));
            }
            position += match.Length;
        }

        expression = new DiceExpression(text, terms);
        return true;
    }

    public DiceResult Roll(IRandomSource random)
    {
        var results = new List<DiceTermResult>(Terms.Count);
        var total = 0;
        foreach (var term in Terms)
        {
            if (term.IsConstant)
            {
                results.Add(new DiceTermResult(term, Array.Empty<int>(), Array.Empty<bool>(), term.Count));
                total += term.Sign * term.Count;
                continue;
            }
            var rolls = new int[term.Count];
            for (var i = 0; i < rolls.Length; i++) rolls[i] = random.Next(term.Sides);

            var kept = new bool[term.Count];
            var order = Enumerable.Range(0, term.Count);
            order = term.KeepHighest ? order.OrderByDescending(i => rolls[i]) : order.OrderBy(i => rolls[i]);
            foreach (var index in order.Take(term.Keep)) kept[index] = true;

            var subtotal = 0;
            for (var i = 0; i < rolls.Length; i++)
                if (kept[i]) subtotal += rolls[i];
            results.Add(new DiceTermResult(term, rolls, kept, subtotal));
            total += term.Sign * subtotal;
        }
        return new DiceResult(Text, results, total);
    }
}
