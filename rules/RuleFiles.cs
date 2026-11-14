using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using RoleDMFable.Core;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace RoleDMFable.Rules;

/// <summary>Lee el texto de un reglamento en PDF, Word (.docx) o texto plano.</summary>
public static class RuleFiles
{
    public static IReadOnlyList<string> Extensions { get; } = new[] { ".pdf", ".docx", ".txt", ".md" };

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    /// <summary>
    /// Lee el archivo y lo trocea. Lanza <see cref="FormatException"/> con un mensaje para el jugador
    /// si el formato no vale o no hay texto. <paramref name="progress"/> recibe de 0 a 1.
    /// </summary>
    public static Rulebook Import(string path, Action<double>? progress = null, CancellationToken cancel = default, BookKind kind = BookKind.Rules)
    {
        var name = Path.GetFileName(path);
        IReadOnlyList<RulePage> pages;
        try
        {
            pages = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".pdf" => ReadPdf(path, progress, cancel),
                ".docx" => ReadDocx(path),
                ".txt" or ".md" => new[] { new RulePage(null, ReadText(path)) },
                ".doc" => throw new FormatException("Los Word antiguos (.doc) no se pueden leer. Ábrelo en Word y guárdalo como .docx o PDF."),
                _ => throw new FormatException("Ese tipo de archivo no vale. Usa PDF, Word (.docx) o TXT."),
            };
        }
        catch (Exception e) when (e is not FormatException and not OperationCanceledException)
        {
            throw new FormatException($"No se pudo leer «{name}»: {e.Message}", e);
        }

        try
        {
            return Rulebook.FromPages(Path.GetFileNameWithoutExtension(path).Replace('_', ' ').Trim(), name, pages, kind);
        }
        catch (FormatException) when (Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            throw new FormatException("Ese PDF no tiene texto: parece escaneado (son fotos de las páginas). Hace falta un PDF con texto seleccionable.");
        }
    }

    private static IReadOnlyList<RulePage> ReadPdf(string path, Action<double>? progress, CancellationToken cancel)
    {
        using var document = PdfDocument.Open(path);
        var pages = new List<RulePage>(document.NumberOfPages);
        for (var number = 1; number <= document.NumberOfPages; number++)
        {
            cancel.ThrowIfCancellationRequested();
            string text;
            try
            {
                text = ContentOrderTextExtractor.GetText(document.GetPage(number));
            }
            catch (Exception)
            {
                // Una página rara no debe impedir leer el resto del libro.
                text = "";
            }
            pages.Add(new RulePage(number, text));
            progress?.Invoke((double)number / document.NumberOfPages);
        }
        return pages;
    }

    private static IReadOnlyList<RulePage> ReadDocx(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        var entry = archive.GetEntry("word/document.xml")
            ?? throw new FormatException("El archivo no es un documento de Word válido.");
        using var stream = entry.Open();
        var document = XDocument.Load(stream);
        XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        var text = new StringBuilder();
        foreach (var paragraph in document.Descendants(w + "p"))
        {
            foreach (var node in paragraph.Descendants())
            {
                if (node.Name == w + "t") text.Append(node.Value);
                else if (node.Name == w + "tab") text.Append(' ');
                else if (node.Name == w + "br" || node.Name == w + "cr") text.Append('\n');
            }
            // Cada párrafo de Word es un párrafo aparte, y las celdas de una tabla también.
            text.Append("\n\n");
        }
        return new[] { new RulePage(null, text.ToString()) };
    }

    private static string ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes).TrimStart('﻿');
        }
        catch (DecoderFallbackException)
        {
            // Archivos de texto antiguos de Windows.
            return Encoding.Latin1.GetString(bytes);
        }
    }
}

/// <summary>Guarda los reglamentos ya leídos en una carpeta, un archivo por libro, para no releerlos cada vez.</summary>
public sealed class RulesStore
{
    private readonly string _folder;

    public RulesStore(string folder) => _folder = folder;

    /// <summary>Carga todo lo guardado. Devuelve los avisos de los archivos que no se pudieron leer.</summary>
    public IReadOnlyList<string> LoadInto(RulesLibrary library)
    {
        var problems = new List<string>();
        if (!Directory.Exists(_folder)) return problems;
        foreach (var file in Directory.GetFiles(_folder, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                library.Add(Rulebook.FromJson(File.ReadAllText(file)));
            }
            catch (Exception e) when (e is FormatException or IOException)
            {
                problems.Add($"No se pudo cargar el reglamento {Path.GetFileName(file)}: {e.Message}");
            }
        }
        return problems;
    }

    public void Save(Rulebook book)
    {
        Directory.CreateDirectory(_folder);
        var target = PathFor(book.Id);
        var temp = target + ".tmp";
        File.WriteAllText(temp, book.ToJson());
        File.Move(temp, target, overwrite: true);
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
    }

    private string PathFor(string id) => Path.Combine(_folder, id + ".json");
}
