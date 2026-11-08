using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;

namespace Datum.Tests;

public sealed class LiveToolsTests
{
    [Fact]
    public async Task EventsSolidWorksRaisesAreListedNewestLast()
    {
        var backend = new FakeBackend();
        using var connection = new SolidWorksConnection(() => backend, TimeSpan.FromSeconds(5));
        await StatusTools.Status(connection, CancellationToken.None);

        backend.Raise("selection", "bracket.SLDPRT", "2 selected");
        backend.Raise("rebuild", "bracket.SLDPRT", "errors in Fillet1");
        var all = StatusToolsTests.TextOf(await LiveTools.Recent(connection));
        var rebuilds = StatusToolsTests.TextOf(await LiveTools.Recent(connection, kind: "rebuild"));

        Assert.EndsWith("rebuild bracket.SLDPRT: errors in Fillet1\n", all, StringComparison.Ordinal);
        Assert.DoesNotContain("selection", rebuilds, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogKeepsOnlyTheNewest()
    {
        var log = new EventLog();
        for (var i = 0; i < 250; i++)
        {
            log.Add(new SolidWorksEvent(DateTimeOffset.Now, "rebuild", $"p{i}", null));
        }

        Assert.Equal("p249", log.Recent(500)[^1].Document);
        Assert.Equal(200, log.Recent(500).Count);
    }
}
