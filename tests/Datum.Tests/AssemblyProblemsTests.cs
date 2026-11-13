using Datum.Core;

namespace Datum.Tests;

public sealed class AssemblyProblemsTests
{
    private static ComponentInfo Part(string name, string status, bool isFixed = false) =>
        new($"comp#{name}", name, null, "Default", isFixed, false, status, new ComponentPose(default, default));

    [Fact]
    public void AnUnderDefinedPartIsFreeToMove()
    {
        var found = Assert.Single(AssemblyProblems.Find([Part("base-1", "fully defined", true), Part("leg-1", "under defined")], [], [], []));

        Assert.True(AssemblyProblems.IsFreeToMove(found));
    }

    [Theory]
    [InlineData("no solution")]
    [InlineData("over defined")]
    [InlineData("invalid solution")]
    public void AnUnsolvedPartIsAProblemNotFreedom(string status)
    {
        var found = Assert.Single(AssemblyProblems.Find([Part("base-1", "fully defined", true), Part("leg-1", status)], [], [], []));

        Assert.False(AssemblyProblems.IsFreeToMove(found));
        Assert.StartsWith($"leg-1 is {status}: its mates can't all be met", found, StringComparison.Ordinal);
    }
}
