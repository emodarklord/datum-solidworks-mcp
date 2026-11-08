using Datum.Core;

namespace Datum.Tests;

public sealed class EquationTests
{
    [Fact]
    public void AVariableAlwaysCarriesMm()
    {
        Assert.Equal("\"Depth\" = 20.5mm", Equations.Variable("Depth", 20.5));
    }

    [Fact]
    public void ADimensionIsDrivenByAnExpression()
    {
        Assert.Equal("\"D1@Boss-Extrude1\" = \"Depth\" * 2", Equations.Driving("D1@Boss-Extrude1", "\"Depth\" * 2"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a\"b")]
    [InlineData("a=b")]
    public void ABadNameIsRefused(string name)
    {
        Assert.Throws<DatumException>(() => Equations.Variable(name, 1));
    }

    [Fact]
    public void TheLeftSideIsTheName()
    {
        Assert.Equal("D1@Boss-Extrude1", Equations.LeftOf("\"D1@Boss-Extrude1\" = \"Depth\""));
    }
}
