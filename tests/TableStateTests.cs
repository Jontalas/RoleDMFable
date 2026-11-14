using RoleDMFable.Core;
using Xunit;

namespace RoleDMFable.Tests;

public class TableStateTests
{
    private static TableState Bridge() =>
        new(SceneSpec.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenes", "sala_del_puente.json"))));

    [Fact]
    public void LoadsTheSampleScene()
    {
        var table = Bridge();
        Assert.Equal(13, table.Width);
        Assert.Equal(9, table.Height);
        Assert.Equal(6, table.Tokens.Count);
        Assert.Equal(Terrain.Wall, table.TerrainAt(0, 0));
        Assert.Equal(Terrain.Water, table.TerrainAt(6, 2));
        Assert.Equal(Terrain.Bridge, table.TerrainAt(6, 4));
        Assert.True(table.IsUnexplored(12, 3));
        Assert.False(table.IsUnexplored(3, 3));
    }

    [Fact]
    public void MovesOntoFloorAndBridge()
    {
        var table = Bridge();
        var changes = 0;
        table.Changed += () => changes++;

        var result = table.MoveToken("kael", 6, 4);

        Assert.True(result.Ok);
        Assert.Equal(10, result.Feet);
        Assert.Equal((6, 4), (table.FindToken("kael")!.X, table.FindToken("kael")!.Y));
        Assert.Equal(1, changes);
    }

    [Theory]
    [InlineData("kael", 6, 2, MoveError.Blocked)]      // agua
    [InlineData("kael", 4, 0, MoveError.Blocked)]      // pared
    [InlineData("kael", 1, 6, MoveError.Blocked)]      // roca
    [InlineData("kael", 2, 3, MoveError.Occupied)]     // Mira
    [InlineData("kael", 13, 4, MoveError.OutOfBounds)]
    [InlineData("kael", -1, 4, MoveError.OutOfBounds)]
    [InlineData("nadie", 3, 3, MoveError.UnknownToken)]
    public void RefusesInvalidMoves(string id, int x, int y, MoveError expected)
    {
        var table = Bridge();
        var changes = 0;
        table.Changed += () => changes++;

        var result = table.MoveToken(id, x, y);

        Assert.Equal(expected, result.Error);
        Assert.NotEmpty(result.Explain());
        Assert.Equal(0, changes);
        Assert.Equal((4, 5), (table.FindToken("kael")!.X, table.FindToken("kael")!.Y));
    }

    [Fact]
    public void MovingToOwnCellIsAllowedAndSilent()
    {
        var table = Bridge();
        var changes = 0;
        table.Changed += () => changes++;
        Assert.True(table.MoveToken("kael", 4, 5).Ok);
        Assert.Equal(0, changes);
    }

    [Fact]
    public void HpIsClamped()
    {
        var table = Bridge();
        Assert.Equal(0, table.SetHp("goblin_1", -5));
        Assert.Equal(31, table.SetHp("kael", 99));
        Assert.Throws<ArgumentException>(() => table.SetHp("nadie", 1));
    }

    [Fact]
    public void RevealClearsFog()
    {
        var table = Bridge();
        table.Reveal(11, 0, 1, 9);
        Assert.False(table.IsUnexplored(11, 4));
        Assert.True(table.IsUnexplored(12, 4));
    }

    [Theory]
    [InlineData(0, 0, 3, 0, 15)]
    [InlineData(0, 0, 1, 1, 5)]
    [InlineData(0, 0, 2, 2, 15)]
    [InlineData(0, 0, 3, 3, 20)]
    [InlineData(0, 0, 4, 2, 25)]
    [InlineData(5, 5, 5, 5, 0)]
    public void DiagonalsAlternateFiveAndTen(int x1, int y1, int x2, int y2, int feet)
    {
        Assert.Equal(feet, GridDistance.Feet(x1, y1, x2, y2));
        Assert.Equal(feet, GridDistance.Feet(x2, y2, x1, y1));
    }

    [Theory]
    [InlineData("{\"id\":\"a\",\"name\":\"A\",\"width\":0,\"height\":3}")]
    [InlineData("{\"id\":\"\",\"name\":\"A\",\"width\":3,\"height\":3}")]
    [InlineData("{\"id\":\"a\",\"name\":\"A\",\"width\":3,\"height\":3,\"tokens\":[{\"id\":\"t\",\"name\":\"T\",\"kind\":\"hero\",\"x\":5,\"y\":0,\"hp\":1,\"maxHp\":1}]}")]
    [InlineData("{\"id\":\"a\",\"name\":\"A\",\"width\":3,\"height\":3,\"tokens\":[{\"id\":\"t\",\"name\":\"T\",\"kind\":\"hero\",\"x\":0,\"y\":0,\"hp\":1,\"maxHp\":1},{\"id\":\"t\",\"name\":\"U\",\"kind\":\"enemy\",\"x\":1,\"y\":0,\"hp\":1,\"maxHp\":1}]}")]
    public void RejectsBrokenScenes(string json)
    {
        Assert.Throws<FormatException>(() => SceneSpec.FromJson(json));
    }

    [Fact]
    public void SceneSurvivesJsonRoundTrip()
    {
        var spec = SceneSpec.FromJson(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "scenes", "sala_del_puente.json")));
        var again = SceneSpec.FromJson(spec.ToJson());
        Assert.Equal(spec.Tokens!.Count, again.Tokens!.Count);
        Assert.Equal(spec.Features!.Count, again.Features!.Count);
        Assert.Equal(Terrain.Bridge, new TableState(again).TerrainAt(7, 4));
    }
}
