using RoleDMFable.Core;
using Xunit;

namespace RoleDMFable.Tests;

public class MapToolsTests
{
    private sealed class One : IRandomSource
    {
        public int Next(int sides) => 1;
    }

    private const string Cellar = """
        {"nombre":"Bodega de la posada",
         "plano":["########","#......#","#..o...#","#......#","#.~~...#","########"],
         "llegada":{"x":1,"y":1},
         "fichas":[{"nombre":"Rata gigante","tipo":"enemigo","x":5,"y":3,"pv":4,"papel":"rata hambrienta"},
                   {"nombre":"Rata gigante","tipo":"enemigo","x":6,"y":3,"pv":4},
                   {"nombre":"Tabernero","tipo":"pnj","x":5,"y":1,"pv":8,"icono":"persona"}]}
        """;

    private static (Campaign Game, MasterToolbox Tools) Start()
    {
        var game = MasterToolboxTests.NewGame();
        return (game, new MasterToolbox(game, new One()));
    }

    [Fact]
    public void CreatingAPlaceBuildsTheMapAndTakesThePartyThere()
    {
        var (game, tools) = Start();
        TravelResult? travel = null;
        tools.Travelled += result => travel = result;
        var heroes = game.Current.Tokens.Where(t => t.Kind == TokenKind.Hero).Select(t => t.Id).ToList();

        var outcome = tools.Execute("crear_lugar", MasterToolboxTests.Input(Cellar));

        Assert.False(outcome.IsError, outcome.Result);
        Assert.NotNull(travel);
        var place = game.Current;
        Assert.Equal("bodega_de_la_posada", place.SceneId);
        Assert.Equal((8, 6), (place.Width, place.Height));
        Assert.Equal(Terrain.Wall, place.TerrainAt(0, 0));
        Assert.Equal(Terrain.Rock, place.TerrainAt(3, 2));
        Assert.Equal(Terrain.Water, place.TerrainAt(2, 4));
        Assert.Equal(Terrain.Floor, place.TerrainAt(1, 1));
        Assert.All(heroes, id => Assert.NotNull(place.FindToken(id)));
        Assert.NotNull(place.FindToken("rata_gigante"));
        Assert.NotNull(place.FindToken("rata_gigante_2"));
        Assert.Equal(TokenKind.Npc, place.FindToken("tabernero")!.Kind);
        Assert.Contains("bodega_de_la_posada", outcome.Result);
    }

    [Fact]
    public void ANewPlaceSurvivesSavingAndLoading()
    {
        var (game, tools) = Start();
        tools.Execute("crear_lugar", MasterToolboxTests.Input(Cellar));

        var loaded = Campaign.FromJson(game.ToJson());

        Assert.Equal("bodega_de_la_posada", loaded.Current.SceneId);
        Assert.Equal(3, loaded.Scenes.Count());
        Assert.Equal(Terrain.Rock, loaded.Current.TerrainAt(3, 2));
        Assert.Equal(4, loaded.Current.FindToken("rata_gigante")!.Hp);
    }

    [Fact]
    public void ADoorLinksBothPlacesBothWays()
    {
        var (game, tools) = Start();
        var first = game.Current;
        var kael = first.FindToken("kael")!;
        var door = first.FreeCellsNear(kael.X, kael.Y, 1)[0];
        var json = Cellar.Replace("\"llegada\"", $$"""
            "puerta":{"x":{{door.X}},"y":{{door.Y}},"etiqueta":"Trampilla"},"llegada"
            """);

        Assert.False(tools.Execute("crear_lugar", MasterToolboxTests.Input(json)).IsError);

        var way = first.ExitAt(door.X, door.Y);
        Assert.NotNull(way);
        Assert.Equal("bodega_de_la_posada", way!.To);
        Assert.Equal("Trampilla", way.Label);
        var back = game.Current.ExitAt(1, 1);
        Assert.Equal(first.SceneId, back!.To);

        // Volver andando: un héroe sale de la casilla de llegada y vuelve a pisarla.
        var hero = game.Current.TokenAt(1, 1)!;
        var free = game.Current.FreeCellsNear(1, 1, 1)[0];
        game.Current.MoveToken(hero.Id, free.X, free.Y);
        game.Current.MoveToken(hero.Id, 1, 1);
        Assert.NotNull(game.TryTravel(hero.Id));
        Assert.Equal(first, game.Current);
    }

    [Theory]
    [InlineData("""{"nombre":"X","plano":["....","....","..","...."],"llegada":{"x":0,"y":0}}""", "medir lo mismo")]
    [InlineData("""{"nombre":"X","plano":["....","..?.","....","...."],"llegada":{"x":0,"y":0}}""", "no válido")]
    [InlineData("""{"nombre":"X","plano":["....","...."],"llegada":{"x":0,"y":0}}""", "filas")]
    [InlineData("""{"nombre":"X","plano":["####","####","####","####"],"llegada":{"x":1,"y":1}}""", "no cabe")]
    [InlineData("""{"nombre":"X","plano":["....","....","....","...."],"llegada":{"x":9,"y":0}}""", "fuera del plano")]
    [InlineData("""{"nombre":"X","plano":["....","#...","....","...."],"llegada":{"x":3,"y":3},"fichas":[{"nombre":"Lobo","tipo":"enemigo","x":0,"y":1,"pv":5}]}""", "no se puede pisar")]
    [InlineData("""{"nombre":"X","plano":["....","....","....","...."],"llegada":{"x":3,"y":3},"fichas":[{"nombre":"Lobo","tipo":"dragon","x":0,"y":1,"pv":5}]}""", "enemigo o pnj")]
    public void ABadPlaceIsRefusedAndNothingChanges(string json, string reason)
    {
        var (game, tools) = Start();
        var before = game.Current;

        var outcome = tools.Execute("crear_lugar", MasterToolboxTests.Input(json));

        Assert.True(outcome.IsError);
        Assert.Contains(reason, outcome.Result);
        Assert.Equal(before, game.Current);
        Assert.Equal(2, game.Scenes.Count());
    }

    [Fact]
    public void GoingBackToAKnownPlace()
    {
        var (game, tools) = Start();
        var first = game.Current.SceneId;
        tools.Execute("crear_lugar", MasterToolboxTests.Input(Cellar));

        Assert.True(tools.Execute("ir_a_lugar", MasterToolboxTests.Input("""{"lugar":"bodega_de_la_posada"}""")).IsError);
        Assert.True(tools.Execute("ir_a_lugar", MasterToolboxTests.Input("""{"lugar":"la luna"}""")).IsError);
        var outcome = tools.Execute("ir_a_lugar", MasterToolboxTests.Input("""{"lugar":"Sala del puente"}"""));

        Assert.False(outcome.IsError, outcome.Result);
        Assert.Equal(first, game.Current.SceneId);
        Assert.NotNull(game.Current.FindToken("kael"));
    }

    [Fact]
    public void TokensComeAndGo()
    {
        var (game, tools) = Start();
        var table = game.Current;
        var kael = table.FindToken("kael")!;
        var free = table.FreeCellsNear(kael.X, kael.Y, 1)[0];

        var placed = tools.Execute("poner_ficha", MasterToolboxTests.Input($$"""{"nombre":"Goblin","tipo":"enemigo","x":{{free.X}},"y":{{free.Y}},"pv":6,"icono":"goblin"}"""));
        Assert.False(placed.IsError, placed.Result);
        var token = table.TokenAt(free.X, free.Y)!;
        Assert.Equal("goblin", token.Id);
        Assert.Equal("goblin", token.Icon);

        // Encima de otra ficha o de un héroe, no.
        Assert.True(tools.Execute("poner_ficha", MasterToolboxTests.Input($$"""{"nombre":"Otro","tipo":"pnj","x":{{kael.X}},"y":{{kael.Y}},"pv":3}""")).IsError);
        Assert.True(tools.Execute("quitar_ficha", MasterToolboxTests.Input("""{"ficha":"kael"}""")).IsError);

        Assert.False(tools.Execute("quitar_ficha", MasterToolboxTests.Input("""{"ficha":"goblin"}""")).IsError);
        Assert.Null(table.FindToken("goblin"));
    }

    [Fact]
    public void RemovingWhoHasTheTurnPassesItOn()
    {
        var (game, tools) = Start();
        var table = game.Current;
        table.StartCombat(new[] { new CombatantSpec("goblin_1", 20), new CombatantSpec("kael", 10), new CombatantSpec("mira", 5) });
        Assert.Equal("goblin_1", table.TurnToken!.Id);

        tools.Execute("quitar_ficha", MasterToolboxTests.Input("""{"ficha":"goblin_1"}"""));

        Assert.Equal("kael", table.TurnToken!.Id);
        Assert.Equal(new[] { "kael", "mira" }, table.Combat!.Order.Select(c => c.Token));
        Assert.Equal("mira", table.NextTurn()!.Id);
    }

    [Fact]
    public void TerrainChangesButNeverUnderAToken()
    {
        var (game, tools) = Start();
        var table = game.Current;
        var kael = table.FindToken("kael")!;

        var blocked = tools.Execute("cambiar_terreno", MasterToolboxTests.Input($$"""{"x":{{kael.X}},"y":{{kael.Y}},"tipo":"muro"}"""));
        Assert.True(blocked.IsError);
        Assert.Equal(Terrain.Floor, table.TerrainAt(kael.X, kael.Y));

        Assert.Equal(Terrain.Wall, table.TerrainAt(0, 0));
        Assert.False(tools.Execute("cambiar_terreno", MasterToolboxTests.Input("""{"x":0,"y":0,"ancho":2,"tipo":"suelo"}""")).IsError);
        Assert.Equal(Terrain.Floor, table.TerrainAt(1, 0));
        Assert.True(tools.Execute("cambiar_terreno", MasterToolboxTests.Input("""{"x":99,"y":0,"tipo":"suelo"}""")).IsError);
        Assert.True(tools.Execute("cambiar_terreno", MasterToolboxTests.Input("""{"x":0,"y":0,"tipo":"lava"}""")).IsError);
    }

    [Fact]
    public void FogHidesANewPlaceUntilRevealed()
    {
        var (game, tools) = Start();
        var wide = """
            {"nombre":"Túnel","niebla":true,"llegada":{"x":1,"y":1},
             "plano":["....................","....................","....................","...................."]}
            """;
        Assert.False(tools.Execute("crear_lugar", MasterToolboxTests.Input(wide)).IsError);
        var table = game.Current;
        Assert.False(table.IsUnexplored(1, 1));
        Assert.True(table.IsUnexplored(19, 3));

        Assert.False(tools.Execute("revelar_zona", MasterToolboxTests.Input("""{"x":15,"y":0,"ancho":5,"alto":4}""")).IsError);
        Assert.False(table.IsUnexplored(19, 3));
    }

    [Fact]
    public void CompanionsLeftBehindCanBeBroughtAlong()
    {
        var (game, tools) = Start();
        var first = game.Current;
        var free = first.FreeCellsNear(first.FindToken("kael")!.X, first.FindToken("kael")!.Y, 1)[0];
        tools.Execute("poner_ficha", MasterToolboxTests.Input($$"""{"nombre":"Tomás","tipo":"pnj","x":{{free.X}},"y":{{free.Y}},"pv":6}"""));

        var created = tools.Execute("crear_lugar", MasterToolboxTests.Input(Cellar));
        Assert.Contains("Han llegado juntos todos los héroes", created.Result);
        Assert.Contains("Tomás (tomas)", created.Result);
        Assert.Null(game.Current.FindToken("tomas"));

        // Los héroes no se traen, y lo que no existe tampoco.
        Assert.True(tools.Execute("traer_ficha", MasterToolboxTests.Input("""{"ficha":"kael","x":2,"y":2}""")).IsError);
        Assert.True(tools.Execute("traer_ficha", MasterToolboxTests.Input("""{"ficha":"nadie","x":2,"y":2}""")).IsError);

        var brought = tools.Execute("traer_ficha", MasterToolboxTests.Input("""{"ficha":"Tomás","x":2,"y":2}"""));
        Assert.False(brought.IsError, brought.Result);
        Assert.NotNull(game.Current.FindToken("tomas"));
        Assert.Null(first.FindToken("tomas"));
        Assert.True(tools.Execute("traer_ficha", MasterToolboxTests.Input("""{"ficha":"tomas","x":2,"y":2}""")).IsError);
    }

    [Fact]
    public void TheTurnTextListsTheOtherPlaces()
    {
        var (game, _) = Start();
        var text = MasterPrompt.BuildTurn(game, "Kael", "miro alrededor");
        Assert.Contains("OTROS LUGARES QUE YA EXISTEN", text);
        Assert.Contains("galeria_de_las_setas", text);
    }
}
