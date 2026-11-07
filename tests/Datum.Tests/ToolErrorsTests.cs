using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;

namespace Datum.Tests;

public sealed class ToolErrorsTests
{
    [Fact]
    public async Task AnUnexpectedFailureStillGivesACodeAndAHint()
    {
        using var connection = new SolidWorksConnection(() => new FakeBackend(), TimeSpan.FromSeconds(5));
        var workspace = new Workspace([Path.GetTempPath()], Path.Combine(Path.GetTempPath(), "datum-backups"));
        await DocumentTools.New(connection, workspace, "part");

        var result = await DocumentTools.Export(connection, workspace, "bad\0name.step");
        var text = StatusToolsTests.TextOf(result);

        Assert.True(result.IsError);
        Assert.StartsWith("UNEXPECTED: ", text, StringComparison.Ordinal);
        Assert.Contains("Hint: Retry once", text, StringComparison.Ordinal);
    }
}
