using System.Text.Json;
using Datum.Core;

namespace Datum.Tests;

public sealed class IntentTests
{
    private static Dictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public async Task AReplaySwapsOldFeatureHandlesForNewOnes()
    {
        var log = new IntentLog();
        log.Add("sketch_end", Args("{}"), "Sketch1 is feat#7");
        log.Add("feature_extrude", Args("""{"sketch":"feat#7","depth":10}"""), "Boss is feat#8");
        var calls = new List<string>();

        await IntentLog.ReplayAsync(IntentLog.Import(log.Export()), (tool, args) =>
        {
            calls.Add($"{tool} {JsonSerializer.Serialize(args)}");
            return Task.FromResult(tool == "sketch_end" ? "Sketch1 is feat#99" : "ok");
        });

        Assert.Equal("""feature_extrude {"sketch":"feat#99","depth":10}""", calls[1]);
    }

    [Fact]
    public async Task AFaceWithNoRecordedGeometryStopsAReplayAndSaysWhy()
    {
        var log = new IntentLog();
        log.Add("sketch_begin", Args("""{"on":"face#12"}"""), "");

        var refused = await Assert.ThrowsAsync<DatumException>(() => IntentLog.ReplayAsync(log.Steps, (_, _) => Task.FromResult("")));

        Assert.Contains("face#12", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FacesAndEdgesAreFoundAgainByTheirGeometry()
    {
        var log = new IntentLog();
        log.Add("sketch_begin", Args("""{"on":"face#12"}"""), "", new() { ["face#12"] = "face:plane 6000 mm² normal +z centre 0,0,20" });
        log.Add("feature_fillet", Args("""{"edges":["edge#40"]}"""), "", new() { ["edge#40"] = "edge:line 100 mm|plane 6000 mm² normal +z centre 0,0,20|plane 2000 mm² normal +y centre 0,30,10" });
        var calls = new List<string>();
        var faces = "face#3 plane 2000 mm² normal +y centre 0,30,10\nface#7 plane 6000 mm² normal +z centre 0,0,20\n";
        var edges = "edge#8 line 100 mm faces face#7,face#9\nedge#9 line 100 mm faces face#7,face#3\n";

        await IntentLog.ReplayAsync(IntentLog.Import(log.Export()), (tool, args) =>
        {
            var json = JsonSerializer.Serialize(args);
            calls.Add($"{tool} {json}");
            return Task.FromResult(tool == "model_faces" ? (json.Contains("edge", StringComparison.Ordinal) ? edges : faces) : "ok");
        });

        Assert.Contains("""sketch_begin {"on":"face#7"}""", calls);
        Assert.Contains("""feature_fillet {"edges":["edge#9"]}""", calls);
    }

    [Theory]
    [InlineData("feature_extrude", true)]
    [InlineData("doc_save", false)]
    [InlineData("model_info", false)]
    public void OnlyModellingStepsAreRecorded(string tool, bool recorded)
    {
        Assert.Equal(recorded, IntentLog.Records(tool));
    }

    [Fact]
    public void GatewayCallsAreTaggedAsSolidWorksOnly()
    {
        var log = new IntentLog();
        log.Add("invoke", Args("{}"), "");

        Assert.Equal("solidworks", log.Steps[0].Tag);
    }
}
