using System.Text.Json;
using RoleDMFable.Core;
using RoleDMFable.Rules;
using Xunit;

namespace RoleDMFable.Tests;

public class RulesTests
{
    private static string Sample(string name) => Path.Combine(AppContext.BaseDirectory, "samples", name);

    private static RulesLibrary LibraryWith(params string[] files)
    {
        var library = new RulesLibrary();
        foreach (var file in files) library.Add(RuleFiles.Import(Sample(file)));
        return library;
    }

    [Fact]
    public void ReadsAPdfWithPageNumbers()
    {
        var progress = new List<double>();
        var book = RuleFiles.Import(Sample("reglas.pdf"), progress.Add);

        Assert.Equal("reglas", book.Title);
        Assert.True(book.HasPages);
        Assert.Equal(1.0, progress[^1]);
        var library = new RulesLibrary();
        library.Add(book);

        // El ejemplo es tan corto que las tres páginas caben en un solo pasaje.
        var hit = library.Search("ataques de oportunidad")[0];
        Assert.Equal("reglas, págs. 1-3", hit.Source);
        Assert.Contains("abandona una casilla", hit.Text);
        Assert.Contains("1d6 puntos de daño por frío", hit.Text);
    }

    [Fact]
    public void RejoinsWordsHyphenatedAtLineEnds()
    {
        var book = RuleFiles.Import(Sample("reglas.pdf"));
        Assert.Contains(book.Passages, p => p.Text.Contains("supera la dificultad, la acción"));
    }

    [Fact]
    public void ReadsWordIncludingTables()
    {
        var library = LibraryWith("reglas.docx");
        Assert.False(library.Books[0].HasPages);
        Assert.Contains("2d4+2", library.Search("poción de curación")[0].Text);
        Assert.Contains("1d4", library.Search("daga daño")[0].Text);
    }

    [Fact]
    public void ReadsPlainTextInUtf8AndOldWindowsEncoding()
    {
        var library = LibraryWith("reglas.txt", "latin1.txt");
        Assert.Contains("ocho horas", library.Search("descanso sueño")[0].Text);
        Assert.Contains("acción por sorpresa", library.Search("emboscada")[0].Text);
    }

    [Fact]
    public void SearchIgnoresAccentsCaseAndPlurals()
    {
        var library = LibraryWith("reglas.pdf", "reglas.docx", "reglas.txt");
        Assert.Contains("iniciativa", library.Search("INICIATIVA")[0].Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("poción", library.Search("pociones de curacion")[0].Text);
        Assert.Equal("reglas", library.Search("flanquear enemigos")[0].Book);
        Assert.Empty(library.Search("teletransporte dimensional"));
        Assert.Empty(library.Search("de la el"));
        Assert.True(library.Search("tirada", max: 2).Count <= 2);
    }

    [Fact]
    public void RefusesFilesWithoutTextOrOfTheWrongKind()
    {
        Assert.Throws<FormatException>(() => RuleFiles.Import(Sample("vacio.txt")));
        Assert.Throws<FormatException>(() => RuleFiles.Import(Sample("viejo.doc")));
        Assert.Throws<FormatException>(() => RuleFiles.Import(Sample("foto.png")));
        Assert.Throws<FormatException>(() => RuleFiles.Import(Sample("no_existe.pdf")));
        Assert.True(RuleFiles.IsSupported("Manual.PDF"));
        Assert.False(RuleFiles.IsSupported("manual.doc"));
    }

    [Fact]
    public void LongTextIsSplitIntoPassagesOfReasonableSize()
    {
        var paragraph = string.Join(' ', Enumerable.Repeat("palabra", 70));
        var pages = Enumerable.Range(1, 10).Select(n => new RulePage(n, $"{paragraph}\n\n{paragraph}\n\n{paragraph}"));
        var book = Rulebook.FromPages("Manual básico", "manual.pdf", pages);

        Assert.Equal("manual_pdf", book.Id);
        Assert.Equal("Manual básico", book.Title);
        Assert.True(book.Passages.Count >= 10);
        Assert.All(book.Passages, p => Assert.InRange(p.Text.Split(' ').Length, 1, Rulebook.PassageWords * 3 / 2));
        Assert.Equal(Enumerable.Range(1, 10), book.Passages.Select(p => p.Page!.Value).Distinct());

        var library = new RulesLibrary();
        library.Add(Rulebook.FromPages("Bestiario", "b.pdf", Enumerable.Range(1, 5).Select(n =>
            new RulePage(n, string.Join(' ', Enumerable.Repeat(n == 4 ? "basilisco" : "relleno", 200))))));
        Assert.Equal("Bestiario, pág. 4", library.Search("basiliscos")[0].Source);

        var huge = Rulebook.FromPages("Sin párrafos", "x.txt", new[] { new RulePage(null, string.Join(' ', Enumerable.Repeat("palabra", 2000))) });
        Assert.All(huge.Passages, p => Assert.InRange(p.Text.Split(' ').Length, 1, Rulebook.PassageWords * 3 / 2));
    }

    [Fact]
    public void BooksSurviveSavingAndLoading()
    {
        var folder = Path.Combine(Path.GetTempPath(), "roledm-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new RulesStore(folder);
            var book = RuleFiles.Import(Sample("reglas.pdf"));
            store.Save(book);
            store.Save(RuleFiles.Import(Sample("reglas.docx")));
            File.WriteAllText(Path.Combine(folder, "roto.json"), "{ esto no es json");

            var library = new RulesLibrary();
            var problems = store.LoadInto(library);

            Assert.Equal(2, library.Books.Count);
            Assert.Single(problems);
            Assert.Equal("reglas, págs. 1-3", library.Search("ataque de oportunidad")[0].Source);

            store.Delete(book.Id);
            var again = new RulesLibrary();
            store.LoadInto(again);
            Assert.Single(again.Books);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void AddingABookTwiceReplacesItAndRemovingForgetsIt()
    {
        var library = LibraryWith("reglas.txt", "reglas.txt");
        Assert.Single(library.Books);
        Assert.True(library.Remove(library.Books[0].Id));
        Assert.Empty(library.Search("descanso"));
        Assert.False(library.Remove("no_existe"));
    }

    [Fact]
    public void TheMasterCanLookRulesUp()
    {
        var game = MasterToolboxTests.NewGame();
        var input = MasterToolboxTests.Input("""{"consulta":"ataque de oportunidad"}""");

        var without = new MasterToolbox(game, new SystemRandomSource(1)).Execute("consultar_reglas", input);
        Assert.False(without.IsError);
        Assert.Contains("No hay ningún reglamento", without.Result);
        Assert.Contains("REGLAMENTOS CARGADOS: ninguno", MasterPrompt.BuildTurn(game, "Kael", "Hola"));

        var library = LibraryWith("reglas.pdf");
        var tools = new MasterToolbox(game, new SystemRandomSource(1), library);
        var found = tools.Execute("consultar_reglas", input);
        Assert.Contains("[reglas, págs. 1-3]", found.Result);
        Assert.Contains("abandona una casilla", found.Result);
        Assert.Contains("No se ha encontrado", tools.Execute("consultar_reglas", MasterToolboxTests.Input("""{"consulta":"teletransporte"}""")).Result);
        Assert.True(tools.Execute("consultar_reglas", MasterToolboxTests.Input("{}")).IsError);
        Assert.Contains("REGLAMENTOS CARGADOS: reglas.", MasterPrompt.BuildTurn(game, "Kael", "Hola", library));
    }
}
