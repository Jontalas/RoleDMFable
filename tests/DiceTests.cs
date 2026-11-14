using RoleDMFable.Core;
using Xunit;

namespace RoleDMFable.Tests;

/// <summary>Devuelve los valores dados en orden; así cada tirada es predecible.</summary>
internal sealed class FixedRandom(params int[] values) : IRandomSource
{
    private int _index;
    public int Next(int sides)
    {
        var value = values[_index++ % values.Length];
        Assert.InRange(value, 1, sides);
        return value;
    }
}

public class DiceTests
{
    [Fact]
    public void SumsDiceAndConstant()
    {
        var result = DiceExpression.Parse("2d6+3").Roll(new FixedRandom(4, 2));
        Assert.Equal(9, result.Total);
        Assert.Equal("2d6+3 → [4, 2] + 3 = 9", result.Describe());
    }

    [Fact]
    public void BareDieMeansOne()
    {
        Assert.Equal(17, DiceExpression.Parse("d20").Roll(new FixedRandom(17)).Total);
    }

    [Fact]
    public void KeepHighestDropsTheLowest()
    {
        var result = DiceExpression.Parse("4d6kh3").Roll(new FixedRandom(3, 1, 6, 4));
        Assert.Equal(13, result.Total);
        Assert.Equal("4d6kh3 → [3, (1), 6, 4] = 13", result.Describe());
    }

    [Fact]
    public void KeepLowestKeepsTheWorst()
    {
        Assert.Equal(5, DiceExpression.Parse("2d20kl1").Roll(new FixedRandom(18, 5)).Total);
    }

    [Fact]
    public void SubtractsTermsAndIgnoresSpacesAndCase()
    {
        var result = DiceExpression.Parse(" 1D20 + 5 - 1d4 ").Roll(new FixedRandom(10, 3));
        Assert.Equal(12, result.Total);
        Assert.Equal("1d20+5-1d4 → [10] + 5 - [3] = 12", result.Describe());
    }

    [Theory]
    [InlineData("")]
    [InlineData("hola")]
    [InlineData("2d")]
    [InlineData("2d6+")]
    [InlineData("2d6 3")]
    [InlineData("0d6")]
    [InlineData("101d6")]
    [InlineData("1d1")]
    [InlineData("2d6kh3")]
    [InlineData("2d6kh0")]
    [InlineData("++2")]
    public void RejectsBadInput(string input)
    {
        Assert.False(DiceExpression.TryParse(input, out var expression, out var error));
        Assert.Null(expression);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void RealRandomStaysInRange()
    {
        var random = new SystemRandomSource(1234);
        var expression = DiceExpression.Parse("3d6");
        for (var i = 0; i < 500; i++) Assert.InRange(expression.Roll(random).Total, 3, 18);
    }
}
