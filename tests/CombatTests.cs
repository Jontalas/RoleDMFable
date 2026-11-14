using RoleDMFable.Core;
using Xunit;

namespace RoleDMFable.Tests;

public class CombatTests
{
    private sealed class Sequence(params int[] values) : IRandomSource
    {
        private int _next;
        public int Next(int sides) => values[_next++ % values.Length];
    }

    private static CombatantSpec[] Order(params (string Id, int Initiative)[] items) =>
        items.Select(i => new CombatantSpec(i.Id, i.Initiative)).ToArray();

    [Fact]
    public void TurnsGoFromHighestInitiativeAndRoundsWrap()
    {
        var table = MasterToolboxTests.NewGame().Current;
        table.StartCombat(Order(("kael", 12), ("goblin_1", 17), ("mira", 12), ("goblin_jefe", 5)));

        Assert.Equal(new[] { "goblin_1", "kael", "mira", "goblin_jefe" }, table.Combat!.Order.Select(c => c.Token));
        Assert.Equal("goblin_1", table.TurnToken!.Id);
        Assert.Equal("kael", table.NextTurn()!.Id);
        Assert.Equal("mira", table.NextTurn()!.Id);
        Assert.Equal("goblin_jefe", table.NextTurn()!.Id);
        Assert.Equal(1, table.Combat.Round);
        Assert.Equal("goblin_1", table.NextTurn()!.Id);
        Assert.Equal(2, table.Combat.Round);
    }

    [Fact]
    public void FallenCombatantsAreSkipped()
    {
        var table = MasterToolboxTests.NewGame().Current;
        table.SetHp("goblin_1", 0);
        table.StartCombat(Order(("goblin_1", 20), ("kael", 10), ("mira", 5)));
        Assert.Equal("kael", table.TurnToken!.Id);

        table.SetHp("mira", 0);
        Assert.Equal("kael", table.NextTurn()!.Id);
        Assert.Equal(2, table.Combat!.Round);

        table.SetHp("kael", 0);
        Assert.Null(table.NextTurn());
    }

    [Fact]
    public void BadCombatsAreRefusedAndEndingWorksOnce()
    {
        var table = MasterToolboxTests.NewGame().Current;
        Assert.Throws<ArgumentException>(() => table.StartCombat(Order()));
        Assert.Throws<ArgumentException>(() => table.StartCombat(Order(("dragon", 10))));
        Assert.Throws<ArgumentException>(() => table.StartCombat(Order(("kael", 10), ("kael", 3))));
        Assert.Null(table.Combat);
        Assert.Null(table.NextTurn());
        Assert.False(table.EndCombat());

        table.StartCombat(Order(("kael", 10)));
        Assert.True(table.EndCombat());
        Assert.Null(table.TurnToken);
    }

    [Fact]
    public void CombatAndSheetsSurviveSaving()
    {
        var game = MasterToolboxTests.NewGame();
        game.Current.StartCombat(Order(("kael", 15), ("goblin_1", 9)));
        game.Current.NextTurn();
        game.Current.SetSheetEntry("kael", "Fuerza", "16");
        game.Current.SetSheetEntry("kael", "Espada larga", "+6, daño 1d8+3");

        var loaded = Campaign.FromJson(game.ToJson());

        Assert.Equal("goblin_1", loaded.Current.TurnToken!.Id);
        Assert.Equal(1, loaded.Current.Combat!.Round);
        Assert.Equal(new[] { new SheetEntry("Fuerza", "16"), new SheetEntry("Espada larga", "+6, daño 1d8+3") }, loaded.Current.FindToken("kael")!.Sheet);
    }

    [Fact]
    public void LeavingThePlaceEndsItsCombatAndSheetsTravelWithHeroes()
    {
        var game = MasterToolboxTests.NewGame();
        var bridge = game.Current;
        bridge.SetSheetEntry("kael", "CA", "18");
        bridge.MoveToken("goblin_1", 8, 5);
        bridge.StartCombat(Order(("kael", 15), ("goblin_1", 9)));
        bridge.MoveToken("kael", 12, 4);

        Assert.NotNull(game.TryTravel("kael"));
        Assert.Null(bridge.Combat);
        Assert.Null(game.Current.Combat);
        Assert.Equal("18", game.Current.FindToken("kael")!.Sheet.Single().Value);
    }

    [Fact]
    public void SheetEntriesAreUpdatedRemovedAndLimited()
    {
        var table = MasterToolboxTests.NewGame().Current;
        var changes = 0;
        table.Changed += () => changes++;

        Assert.True(table.SetSheetEntry("mira", " Inteligencia ", "17"));
        Assert.True(table.SetSheetEntry("mira", "inteligencia", "18"));
        Assert.True(table.SetSheetEntry("mira", "inteligencia", "18"));
        Assert.Equal(new SheetEntry("Inteligencia", "18"), table.FindToken("mira")!.Sheet.Single());
        Assert.Equal(2, changes);

        Assert.True(table.SetSheetEntry("mira", "Inteligencia", "  "));
        Assert.Empty(table.FindToken("mira")!.Sheet);
        Assert.False(table.SetSheetEntry("mira", "   ", "5"));
        Assert.Throws<ArgumentException>(() => table.SetSheetEntry("dragon", "CA", "30"));

        for (var i = 0; i < TableState.MaxSheetEntries; i++) Assert.True(table.SetSheetEntry("mira", $"Dato {i}", "1"));
        Assert.False(table.SetSheetEntry("mira", "Uno más", "1"));
        Assert.True(table.SetSheetEntry("mira", "Dato 3", new string('x', 500)));
        Assert.Equal(TableState.MaxSheetValueLength, table.FindToken("mira")!.Sheet[3].Value.Length);

        table.ReplaceSheet("mira", new[] { new SheetEntry("CA", "12"), new SheetEntry("", "x"), new SheetEntry("ca", "13"), new SheetEntry("PG", "") });
        Assert.Equal(new[] { new SheetEntry("ca", "13") }, table.FindToken("mira")!.Sheet);
    }

    [Fact]
    public void TheMasterRunsACombatWithTools()
    {
        var game = MasterToolboxTests.NewGame();
        var tools = new MasterToolbox(game, new Sequence(5, 18, 11));
        var seen = new List<string>();
        tools.Announced += (who, text) => seen.Add($"{who}: {text}");

        var start = tools.Execute("iniciar_combate", MasterToolboxTests.Input(
            """{"participantes":[{"ficha":"kael","iniciativa":"1d20+2"},{"ficha":"goblin_1","iniciativa":"1d20"},{"ficha":"Mira","iniciativa":"1d20+1"}]}"""));

        Assert.False(start.IsError);
        Assert.Equal(new[] { "goblin_1", "mira", "kael" }, game.Current.Combat!.Order.Select(c => c.Token));
        Assert.Contains("llevas tú", start.Result);
        Assert.Equal(4, seen.Count);
        Assert.Contains("Orden: Goblin (18), Mira (12), Kael (7)", seen[3]);

        var next = tools.Execute("siguiente_turno", MasterToolboxTests.Input("{}"));
        Assert.Contains("Mira", next.Result);
        Assert.Contains("deja que decida su jugador", next.Result);
        tools.Execute("siguiente_turno", MasterToolboxTests.Input("{}"));
        Assert.Contains("Empieza la ronda 2", tools.Execute("siguiente_turno", MasterToolboxTests.Input("{}")).Result);

        var state = MasterPrompt.DescribeTable(game.Current);
        Assert.Contains("COMBATE EN MARCHA, ronda 2", state);
        Assert.Contains("Tiene el turno: goblin_1", state);

        Assert.False(tools.Execute("terminar_combate", MasterToolboxTests.Input("{}")).IsError);
        Assert.True(tools.Execute("terminar_combate", MasterToolboxTests.Input("{}")).IsError);
        Assert.True(tools.Execute("siguiente_turno", MasterToolboxTests.Input("{}")).IsError);
        Assert.Contains("No hay combate en marcha", MasterPrompt.DescribeTable(game.Current));
    }

    [Fact]
    public void ABadCombatRequestRollsNothing()
    {
        var game = MasterToolboxTests.NewGame();
        var tools = new MasterToolbox(game, new Sequence(10));
        var announced = 0;
        tools.Announced += (_, _) => announced++;

        Assert.True(tools.Execute("iniciar_combate", MasterToolboxTests.Input("""{"participantes":[]}""")).IsError);
        Assert.True(tools.Execute("iniciar_combate", MasterToolboxTests.Input("""{"participantes":[{"ficha":"kael","iniciativa":"1d20"},{"ficha":"dragon","iniciativa":"1d20"}]}""")).IsError);
        Assert.True(tools.Execute("iniciar_combate", MasterToolboxTests.Input("""{"participantes":[{"ficha":"kael","iniciativa":"mucha"}]}""")).IsError);
        Assert.True(tools.Execute("iniciar_combate", MasterToolboxTests.Input("""{"participantes":[{"ficha":"kael","iniciativa":"1d20"},{"ficha":"Kael","iniciativa":"1d20"}]}""")).IsError);
        Assert.Equal(0, announced);
        Assert.Null(game.Current.Combat);
    }

    [Fact]
    public void TheMasterWritesSheetsAndSeesThem()
    {
        var game = MasterToolboxTests.NewGame();
        var tools = new MasterToolbox(game, new Sequence(10));

        Assert.False(tools.Execute("anotar_ficha", MasterToolboxTests.Input("""{"ficha":"kael","dato":"Fuerza","valor":"16"}""")).IsError);
        Assert.False(tools.Execute("anotar_ficha", MasterToolboxTests.Input("""{"ficha":"kael","dato":"Nivel","valor":3}""")).IsError);
        Assert.Contains("Hoja: Fuerza: 16; Nivel: 3", MasterPrompt.DescribeTable(game.Current));

        Assert.Contains("Borrado", tools.Execute("anotar_ficha", MasterToolboxTests.Input("""{"ficha":"kael","dato":"Nivel","valor":""}""")).Result);
        Assert.Single(game.Current.FindToken("kael")!.Sheet);
        Assert.True(tools.Execute("anotar_ficha", MasterToolboxTests.Input("""{"ficha":"dragon","dato":"CA","valor":"30"}""")).IsError);
    }
}
