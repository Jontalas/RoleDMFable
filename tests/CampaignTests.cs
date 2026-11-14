using RoleDMFable.Core;
using Xunit;

namespace RoleDMFable.Tests;

public class CampaignTests
{
    private static SceneSpec Load(string id) =>
        SceneSpec.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenes", id + ".json")));

    private static Campaign NewGame() =>
        new("Prueba", new[] { Load("sala_del_puente"), Load("galeria_de_las_setas") }, "sala_del_puente");

    private static void WalkToEastExit(Campaign game)
    {
        Assert.True(game.Current.MoveToken("goblin_1", 8, 5).Ok); // despeja la salida del puente
        Assert.True(game.Current.MoveToken("kael", 12, 4).Ok);
    }

    [Fact]
    public void HeroesRevealFogAsTheyAdvance()
    {
        var game = NewGame();
        Assert.True(game.Current.IsUnexplored(11, 4));
        game.Current.MoveToken("goblin_1", 8, 5);
        game.Current.MoveToken("kael", 8, 4);
        Assert.False(game.Current.IsUnexplored(12, 4));
        Assert.False(game.Current.IsUnexplored(12, 0));
    }

    [Fact]
    public void EnemiesDoNotRevealFog()
    {
        var game = NewGame();
        game.Current.MoveToken("goblin_2", 10, 2);
        Assert.True(game.Current.IsUnexplored(12, 2));
    }

    [Fact]
    public void StandingOnAnExitMovesTheWholePartyOnly()
    {
        var game = NewGame();
        var changes = 0;
        game.Changed += () => changes++;
        WalkToEastExit(game);
        changes = 0;

        var travel = game.TryTravel("kael");

        Assert.NotNull(travel);
        Assert.Equal("Galería de las setas", travel!.ToScene);
        Assert.Equal(new[] { "Kael", "Mira", "Bruna" }, travel.Travellers);
        Assert.Equal("galeria_de_las_setas", game.Current.SceneId);
        Assert.True(changes > 0);

        var kael = game.Current.FindToken("kael")!;
        Assert.Equal((0, 4), (kael.X, kael.Y));
        var cells = new[] { "kael", "mira", "bruna" }.Select(id => game.Current.FindToken(id)!).Select(t => (t.X, t.Y)).ToList();
        Assert.Equal(3, cells.Distinct().Count());
        Assert.All(cells, c => Assert.True(TableState.IsWalkable(game.Current.TerrainAt(c.X, c.Y))));

        var sala = game.Scenes.First(s => s.SceneId == "sala_del_puente");
        Assert.DoesNotContain(sala.Tokens, t => t.Kind == TokenKind.Hero);
        Assert.Equal(3, sala.Tokens.Count); // los goblins se quedan
        Assert.False(game.Current.IsUnexplored(3, 4));
        Assert.True(game.Current.IsUnexplored(10, 4));
    }

    [Fact]
    public void NoTravelOffAnExitOrForEnemies()
    {
        var game = NewGame();
        Assert.Null(game.TryTravel("kael"));
        game.Current.MoveToken("goblin_1", 12, 4);
        Assert.Null(game.TryTravel("goblin_1"));
        Assert.Null(game.TryTravel("nadie"));
        Assert.Equal("sala_del_puente", game.Current.SceneId);
    }

    [Fact]
    public void PartyCanComeBack()
    {
        var game = NewGame();
        WalkToEastExit(game);
        game.TryTravel("kael");
        // Kael llegó a la casilla de salida de vuelta: basta con viajar otra vez.
        var back = game.TryTravel("kael");
        Assert.NotNull(back);
        Assert.Equal("sala_del_puente", game.Current.SceneId);
        Assert.Equal((12, 4), (game.Current.FindToken("kael")!.X, game.Current.FindToken("kael")!.Y));
    }

    [Fact]
    public void SaveAndLoadKeepEverything()
    {
        var game = NewGame();
        WalkToEastExit(game);
        game.TryTravel("kael");
        game.Current.SetHp("kael", 20);
        game.Current.MoveToken("mira", 3, 3);
        game.AddLog("Kael", "tira 1d20 → [17] = 17");

        var loaded = Campaign.FromJson(game.ToJson());

        Assert.Equal("Prueba", loaded.Name);
        Assert.Equal("galeria_de_las_setas", loaded.Current.SceneId);
        Assert.Equal(20, loaded.Current.FindToken("kael")!.Hp);
        Assert.Equal((3, 3), (loaded.Current.FindToken("mira")!.X, loaded.Current.FindToken("mira")!.Y));
        Assert.Equal(1, loaded.Current.FindToken("kael")!.Seat);
        Assert.Equal(new LogEntry("Kael", "tira 1d20 → [17] = 17"), Assert.Single(loaded.Log));
        Assert.Equal(game.ToJson(), loaded.ToJson());

        var sala = loaded.Scenes.First(s => s.SceneId == "sala_del_puente");
        Assert.False(sala.IsUnexplored(12, 4));
        Assert.Equal(Terrain.Bridge, sala.TerrainAt(6, 4));
        Assert.Equal((8, 5), (sala.FindToken("goblin_1")!.X, sala.FindToken("goblin_1")!.Y));
        Assert.NotNull(sala.ExitAt(12, 4));
    }

    [Theory]
    [InlineData("")]
    [InlineData("esto no es json")]
    [InlineData("null")]
    [InlineData("{\"version\":1,\"name\":\"x\",\"currentScene\":\"a\",\"scenes\":[]}")]
    [InlineData("{\"version\":99,\"name\":\"x\",\"currentScene\":\"a\",\"scenes\":[{\"id\":\"a\",\"name\":\"A\",\"width\":2,\"height\":2}]}")]
    [InlineData("{\"version\":1,\"name\":\"x\",\"currentScene\":\"otra\",\"scenes\":[{\"id\":\"a\",\"name\":\"A\",\"width\":2,\"height\":2}]}")]
    [InlineData("{\"version\":1,\"name\":\"x\",\"currentScene\":\"a\",\"scenes\":[{\"id\":\"a\",\"name\":\"A\",\"width\":2,\"height\":2,\"exits\":[{\"x\":0,\"y\":0,\"to\":\"b\",\"toX\":0,\"toY\":0}]}]}")]
    public void RejectsBrokenSaves(string json)
    {
        Assert.Throws<FormatException>(() => Campaign.FromJson(json));
    }

    [Fact]
    public void TravelIsRefusedWhenThePartyDoesNotFit()
    {
        var a = new SceneSpec("a", "A", 3, 1,
            Tokens: new[]
            {
                new TokenSpec("h1", "Uno", TokenKind.Hero, 0, 0, 1, 1),
                new TokenSpec("h2", "Dos", TokenKind.Hero, 1, 0, 1, 1),
            },
            Exits: new[] { new ExitSpec(0, 0, "b", 0, 0) });
        var b = new SceneSpec("b", "B", 2, 1, Features: new[] { new FeatureSpec(Terrain.Wall, 1, 0) });
        var game = new Campaign("x", new[] { a, b }, "a");

        Assert.Null(game.TryTravel("h1"));
        Assert.Equal("a", game.Current.SceneId);
        Assert.Equal(2, game.Current.Tokens.Count);
    }

    [Fact]
    public void LogIsCapped()
    {
        var game = NewGame();
        for (var i = 0; i < Campaign.MaxLogEntries + 20; i++) game.AddLog("Mesa", i.ToString());
        Assert.Equal(Campaign.MaxLogEntries, game.Log.Count);
        Assert.Equal("20", game.Log[0].Text);
    }
}
