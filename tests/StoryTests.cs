using RoleDMFable.Core;
using RoleDMFable.Rules;
using Xunit;

namespace RoleDMFable.Tests;

public class StoryTests
{
    private sealed class Dice(params int[] values) : IRandomSource
    {
        private int _next;
        public int Next(int sides) => Math.Min(values[_next++ % values.Length], sides);
    }

    [Theory]
    [InlineData(1, Likelihood.Even, OracleAnswer.NoAnd)]
    [InlineData(5, Likelihood.Even, OracleAnswer.No)]
    [InlineData(6, Likelihood.Even, OracleAnswer.Yes)]
    [InlineData(10, Likelihood.Even, OracleAnswer.YesAnd)]
    [InlineData(4, Likelihood.Likely, OracleAnswer.Yes)]       // 4 + mitad de 3 redondeada arriba = 6
    [InlineData(4, Likelihood.VeryLikely, OracleAnswer.Yes)]   // 4 + 3 = 7
    [InlineData(7, Likelihood.Unlikely, OracleAnswer.No)]      // 7 - 2 = 5
    [InlineData(3, Likelihood.VeryUnlikely, OracleAnswer.NoAnd)] // 3 - 3 queda en 1
    [InlineData(9, Likelihood.VeryLikely, OracleAnswer.YesAnd)] // no pasa de 10
    public void TheOracleAnswersAccordingToLikelihood(int d10, Likelihood likelihood, OracleAnswer expected)
    {
        var story = new StoryState();
        // d8 = 8 y d6 = 3: sin dados iguales salvo que el d10 sea 3 u 8, que aquí solo importa para el evento.
        var result = story.Ask(likelihood, new Dice(d10, 8, 3, 2, 2, 2));
        Assert.Equal(expected, result.Answer);
        Assert.Equal("alto", result.Degree);
    }

    [Fact]
    public void MatchingDiceBringAnEventAndStoryPoints()
    {
        var story = new StoryState();
        Assert.Null(story.Ask(Likelihood.Even, new Dice(7, 2, 4)).Event);
        Assert.Equal(0, story.Points);

        var pair = story.Ask(Likelihood.Even, new Dice(4, 4, 1, /* evento: */ 5, 4, 1)).Event!;
        Assert.False(pair.Major);
        Assert.Equal("negativo para el grupo", pair.Tone);
        Assert.Contains("personaje nuevo", pair.Subject);
        Assert.Contains("interrumpiendo", pair.Timing);
        Assert.Equal(2, story.Points);

        var triple = story.Ask(Likelihood.Even, new Dice(6, 6, 6, /* evento: */ 9, 8, 6)).Event!;
        Assert.True(triple.Major);
        Assert.Equal("muy positivo para el grupo", triple.Tone);
        Assert.Equal(5, story.Points);
    }

    [Fact]
    public void TheTurningPointStopsPointsAndChangesTheEvents()
    {
        var story = new StoryState(new StorySave(Scene: 6, Points: 20, TurningPoint: 25));
        Assert.False(story.PastTurningPoint);
        Assert.Contains("planteamiento", MasterPrompt.DescribeArc(story));

        var voluntary = story.VoluntaryEvent(major: false, new Dice(8, 8, 3))!;
        Assert.Equal(6, voluntary.PointsGained);
        Assert.Contains("ajeno", voluntary.Subject);
        Assert.True(story.PastTurningPoint);
        Assert.Contains("desenlace", MasterPrompt.DescribeArc(story));

        Assert.Null(story.VoluntaryEvent(major: true, new Dice(1)));
        story.NextScene("El puente");
        var after = story.VoluntaryEvent(major: true, new Dice(2, 8, 5))!;
        Assert.Equal(0, after.PointsGained);
        Assert.Contains("Giro de guion", after.Subject);
        Assert.Equal(26, story.Points);
    }

    [Fact]
    public void NpcReactionsShiftWithGoodwill()
    {
        var story = new StoryState();
        var cold = story.React(Likelihood.VeryUnlikely, new Dice(5, 3, 4, 2));
        Assert.Equal("violenta", cold.Attitude);
        Assert.Contains("quejosa", cold.Personality);
        Assert.Contains("a la altura", cold.Capability);
        Assert.Equal("afectuosa", story.React(Likelihood.VeryLikely, new Dice(7, 1, 6, 1)).Attitude);
    }

    [Fact]
    public void NotesGoalsAndCountdownsAreKeptTidy()
    {
        var story = new StoryState();
        var changes = 0;
        story.Changed += () => changes++;

        Assert.True(story.AddGoal("Cruzar el puente"));
        Assert.True(story.AddGoal("cruzar el puente"));
        Assert.Single(story.Goals);
        Assert.False(story.AddGoal("   "));
        Assert.True(story.AddEnemy("Jefe goblin, quiere el peaje"));
        Assert.True(story.RemoveEnemy("jefe goblin"));
        Assert.False(story.RemoveEnemy("dragón"));
        for (var i = 0; i < StoryState.MaxListEntries + 3; i++) story.AddNote($"Pista {i}");
        Assert.Equal(StoryState.MaxListEntries, story.Notes.Count);
        Assert.Equal("Pista 3", story.Notes[0]);

        Assert.Equal(new Countdown("El ritual", 4, 4), story.StartCountdown("El ritual", 4));
        Assert.Equal(3, story.AdvanceCountdown("el ritual")!.Left);
        Assert.Equal(0, story.AdvanceCountdown("El ritual", 9)!.Left);
        Assert.Null(story.AdvanceCountdown("otra"));
        Assert.Equal(4, story.StartCountdown("El ritual", 4)!.Left);
        Assert.Single(story.Countdowns);
        for (var i = 0; i < StoryState.MaxCountdowns; i++) story.StartCountdown($"Reloj {i}", 3);
        Assert.Equal(StoryState.MaxCountdowns, story.Countdowns.Count);
        Assert.True(story.RemoveCountdown("el ritual"));
        Assert.True(changes > 10);
    }

    [Fact]
    public void TheStoryIsSavedWithTheGameAndOldSavesStillLoad()
    {
        var game = MasterToolboxTests.NewGame();
        game.Story.SetFinalGoal("Recuperar la campana de la mina");
        game.Story.AddAlly("Viejo minero");
        game.Story.StartCountdown("Sube el agua", 5);
        game.Story.AdvanceCountdown("Sube el agua");
        game.Story.NextScene("La galería");

        var loaded = Campaign.FromJson(game.ToJson());
        Assert.Equal(2, loaded.Story.Scene);
        Assert.Equal("La galería", loaded.Story.SceneTitle);
        Assert.Equal("Recuperar la campana de la mina", loaded.Story.FinalGoal);
        Assert.Equal(new Countdown("Sube el agua", 4, 5), loaded.Story.Countdowns.Single());

        var old = System.Text.Json.Nodes.JsonNode.Parse(game.ToJson())!.AsObject();
        old.Remove("story");
        var fresh = Campaign.FromJson(old.ToJsonString());
        Assert.Equal(1, fresh.Story.Scene);
        Assert.Equal(StoryState.MediumAdventure, fresh.Story.TurningPoint);
    }

    [Fact]
    public void TheMasterUsesTheStoryTools()
    {
        var game = MasterToolboxTests.NewGame();
        var tools = new MasterToolbox(game, new Dice(8, 2, 5, 3));
        var seen = new List<string>();
        tools.Announced += (who, text) => seen.Add(text);

        var answer = tools.Execute("preguntar_oraculo", MasterToolboxTests.Input("""{"pregunta":"¿Vigilan el puente?","probabilidad":"probable","detalle":"cantidad"}"""));
        Assert.Contains("→ Sí", answer.Result);
        Assert.Contains("Grado de cantidad: bajo", answer.Result);
        Assert.True(tools.Execute("preguntar_oraculo", MasterToolboxTests.Input("""{"pregunta":"¿Llueve?","probabilidad":"quizá"}""")).IsError);

        Assert.Contains("actitud inicial", tools.Execute("reaccion_pnj", MasterToolboxTests.Input("""{"pnj":"Viejo minero","buen_trato":"igual"}""")).Result);
        Assert.Contains("Palabras:", tools.Execute("inspiracion", MasterToolboxTests.Input("{}")).Result);

        Assert.Contains("Evento inesperado", tools.Execute("evento_inesperado", MasterToolboxTests.Input("""{"importante":false}""")).Result);
        Assert.True(tools.Execute("evento_inesperado", MasterToolboxTests.Input("""{"importante":true}""")).IsError);
        Assert.Contains("escena 2", tools.Execute("nueva_escena", MasterToolboxTests.Input("""{"titulo":"El otro lado"}""")).Result);
        Assert.False(tools.Execute("evento_inesperado", MasterToolboxTests.Input("""{"importante":true}""")).IsError);

        tools.Execute("anotar_historia", MasterToolboxTests.Input("""{"tipo":"objetivo_final","texto":"Cruzar la mina"}"""));
        tools.Execute("anotar_historia", MasterToolboxTests.Input("""{"tipo":"enemigo","texto":"Jefe goblin"}"""));
        tools.Execute("anotar_historia", MasterToolboxTests.Input("""{"tipo":"nota","texto":"Mira teme al agua"}"""));
        Assert.Equal("Tachado.", tools.Execute("anotar_historia", MasterToolboxTests.Input("""{"tipo":"enemigo","texto":"goblin","tachar":true}""")).Result);
        Assert.True(tools.Execute("anotar_historia", MasterToolboxTests.Input("""{"tipo":"rumor","texto":"x"}""")).IsError);

        Assert.False(tools.Execute("cuenta_atras", MasterToolboxTests.Input("""{"accion":"crear","nombre":"Llegan refuerzos","pasos":3}""")).IsError);
        tools.Execute("cuenta_atras", MasterToolboxTests.Input("""{"accion":"avanzar","nombre":"llegan refuerzos","pasos":2}"""));
        Assert.Contains("ha llegado a cero", tools.Execute("cuenta_atras", MasterToolboxTests.Input("""{"accion":"avanzar","nombre":"Llegan refuerzos"}""")).Result);
        Assert.True(tools.Execute("cuenta_atras", MasterToolboxTests.Input("""{"accion":"avanzar","nombre":"otra"}""")).IsError);
        Assert.Equal(3, seen.Count);

        var turn = MasterPrompt.BuildTurn(game, "Kael", "Sigo adelante.");
        Assert.Contains("Escena 2: El otro lado", turn);
        Assert.Contains("Objetivo final: Cruzar la mina", turn);
        Assert.Contains("Notas: Mira teme al agua.", turn);
        Assert.Contains("Cuenta atrás «Llegan refuerzos»: quedan 0 de 3.", turn);
        Assert.DoesNotContain("Enemigos:", turn);
    }

    [Fact]
    public void GuidesAndRulesAreSearchedSeparately()
    {
        var sample = Path.Combine(AppContext.BaseDirectory, "samples");
        var library = new RulesLibrary();
        library.Add(RuleFiles.Import(Path.Combine(sample, "reglas.pdf")));
        var game = MasterToolboxTests.NewGame();
        var tools = new MasterToolbox(game, new Dice(3), library);
        var query = MasterToolboxTests.Input("""{"consulta":"niebla mágica descanso"}""");

        Assert.Contains("No hay guías", tools.Execute("consultar_guia", query).Result);

        library.Add(RuleFiles.Import(Path.Combine(sample, "reglas.txt"), kind: BookKind.Guide));
        Assert.Contains("ocho horas", tools.Execute("consultar_guia", query).Result);
        Assert.Contains("No se ha encontrado", tools.Execute("consultar_reglas", query).Result);
        Assert.DoesNotContain("ocho horas", tools.Execute("consultar_reglas", MasterToolboxTests.Input("""{"consulta":"ataque de oportunidad descanso"}""")).Result);

        var turn = MasterPrompt.BuildTurn(game, "Kael", "Hola", library);
        Assert.Contains("REGLAMENTOS CARGADOS: reglas.", turn);
        Assert.Contains("GUÍAS DE DIRECCIÓN CARGADAS: reglas.", turn);

        var reloaded = Rulebook.FromJson(library.BooksOf(BookKind.Guide).Single().ToJson());
        Assert.Equal(BookKind.Guide, reloaded.Kind);
    }
}
