using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class MotionToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-motion-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public MotionToolsTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(5));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
    }

    private string Gif => Path.Combine(_root, "Datum", "spin.gif");

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AStudyRunsAndReportsOnlyWhatMoved()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));
        Ok(await MotionTools.New(_connection, _workspace, 2, "Spin"));
        Ok(await MotionTools.Motor(_connection, _workspace, "Spin", "face#1", rpm: 60));

        var run = Ok(await MotionTools.Run(_connection, "Spin", samples: 2));

        Assert.Equal("1 s wheel-1: turned 90°, moved 0 mm\n2 s wheel-1: turned 90°, moved 0 mm\n1 components didn't move\n", run);
    }

    [Fact]
    public async Task AStudyCanBeSampledAtGivenTimes()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));
        Ok(await MotionTools.New(_connection, _workspace, 2, "Spin"));

        var run = Ok(await MotionTools.Run(_connection, "Spin", times: [1.5, 0.5]));
        var late = Error(await MotionTools.Run(_connection, "Spin", times: [3]));

        Assert.StartsWith("0.5 s wheel-1: turned 90°, moved 0 mm\n1.5 s wheel-1:", run, StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: give 1 to 40 times between 0 and the study's 2 s", late, StringComparison.Ordinal);
    }

    [Fact]
    public void ASampleSaysWhenSolidWorksStoppedElsewhere()
    {
        Assert.Equal("0.5 s (SOLIDWORKS stopped at 0.75 s) leg-1: turned 45°, moved 0 mm", new MotionSample(0.5, "leg-1", 45, 0) { Reached = 0.75 }.ToString());
    }

    [Fact]
    public async Task AnMp4ExportGivesItsLengthAndFraming()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));
        Ok(await MotionTools.New(_connection, _workspace, 8, "Walk"));

        var reply = Ok(await MotionTools.Export(_connection, _workspace, "Walk", Path.Combine(_root, "Datum", "walk.mp4"), fps: 20, width: 1280));

        Assert.StartsWith("Exported ", reply, StringComparison.Ordinal);
        Assert.Contains(", 1280 x 960 at 20 fps\nIt runs 8.05 s; SOLIDWORKS writes both end frames", reply, StringComparison.Ordinal);
        Assert.EndsWith("WARNING: the check frames came out blank, so the model may be out of view\n", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExportWritesAGifOfEveryFrame()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));
        Ok(await MotionTools.New(_connection, _workspace, 1, "Spin"));

        var reply = Ok(await MotionTools.Export(_connection, _workspace, "Spin", Gif, fps: 4, width: 40));

        Assert.StartsWith("Exported 5 frames to ", reply, StringComparison.Ordinal);
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(Gif), 0, 6));
    }

    [Fact]
    public async Task BadInputsChangeNothing()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));

        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await MotionTools.New(_connection, _workspace, 0)), StringComparison.Ordinal);
        Assert.StartsWith("NOT_FOUND: no motion study", Error(await MotionTools.Run(_connection, "Nope")), StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await MotionTools.Motor(_connection, _workspace, "Spin", "face#1")), StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await MotionTools.Motor(_connection, _workspace, "Spin", "face#1", rpm: 10, swing: 5)), StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await MotionTools.Motor(_connection, _workspace, "Spin", "face#1", swing: 5)), StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: unknown motion path", Error(await MotionTools.AddPath(_connection, _workspace, "Spin", "fly", 1)), StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await MotionTools.Export(_connection, _workspace, "Spin", Path.Combine(_root, "Datum", "spin.png"))), StringComparison.Ordinal);
    }

    [Fact]
    public void MotionOnAPartIsRefused()
    {
        Assert.Throws<DatumException>(() => new MotionPathSpec("explode", 0, 0).Validate());
    }

    [Theory]
    [InlineData("gear", null, "2:1", null, null, true)]
    [InlineData("gear", null, null, null, null, false)]
    [InlineData("gear", null, "half", null, null, false)]
    [InlineData("coincident", null, "2:1", null, null, false)]
    [InlineData("angle", 45d, null, 30d, 60d, true)]
    [InlineData("angle", 90d, null, 30d, 60d, false)]
    [InlineData("angle", 45d, null, 30d, null, false)]
    [InlineData("coincident", null, null, 1d, 2d, false)]
    public void MateRatiosAndLimitsAreChecked(string kind, double? value, string? ratio, double? min, double? max, bool valid)
    {
        var spec = new MateSpec(kind, "a", "b", value, "closest", false) { Ratio = ratio, Min = min, Max = max };

        if (valid)
        {
            spec.Validate();
        }
        else
        {
            Assert.Throws<DatumException>(spec.Validate);
        }
    }

    [Theory]
    [InlineData("red", 0.815, 0.07, 0.106)]
    [InlineData("#000000", 0, 0, 0)]
    [InlineData("#FFFFFF", 1, 1, 1)]
    public void ColoursParse(string text, double red, double green, double blue)
    {
        var colour = Colour.Parse(text);

        Assert.Equal(red, colour.Red, 2);
        Assert.Equal(green, colour.Green, 2);
        Assert.Equal(blue, colour.Blue, 2);
    }

    [Fact]
    public void AnUnknownColourIsRefused()
    {
        Assert.Throws<DatumException>(() => Colour.Parse("mauve"));
    }

    [Fact]
    public void SweepsAndLoftsCheckTheirInputs()
    {
        new SweepSpec("feat#1", null, 25.4, 2, true).Validate();
        new SweepSpec("feat#1", "feat#2", null, null, true).Validate();
        new LoftSpec(["feat#1", "feat#2"], [], true).Validate();

        Assert.Throws<DatumException>(new SweepSpec("feat#1", "feat#2", 10, null, true).Validate);
        Assert.Throws<DatumException>(new SweepSpec("feat#1", null, null, null, true).Validate);
        Assert.Throws<DatumException>(new SweepSpec("feat#1", null, 10, 5, true).Validate);
        Assert.Throws<DatumException>(new SweepSpec("feat#1", "feat#2", null, 1, true).Validate);
        Assert.Throws<DatumException>(new LoftSpec(["feat#1"], [], true).Validate);
    }

    [Fact]
    public void ASplineTakesPointsAndA3dSketchTakesFewerKinds()
    {
        SketchSpecs.Validate([new SketchEntitySpec { Type = "spline", Points = [[0, 0], [1, 1, 1]] }]);

        Assert.Throws<DatumException>(() => SketchSpecs.Validate([new SketchEntitySpec { Type = "spline", Points = [[0, 0]] }]));
        Assert.True(SketchSpecs.Works3D("polyline"));
        Assert.False(SketchSpecs.Works3D("circle"));
    }

    [Fact]
    public void AGifDecodesBackToItsPixels()
    {
        var pixels = new byte[300 * 3];
        Array.Fill(pixels, (byte)255);
        var bytes = Datum.Core.Gif.Encode([new Raster(30, 10, pixels), new Raster(30, 10, new byte[300 * 3])], 10);

        Assert.Equal((byte)';', bytes[^1]);
        Assert.Equal(2, CountImages(bytes));
    }

    // Each frame starts with an image separator straight after its 8-byte control block.
    private static int CountImages(byte[] gif) =>
        Enumerable.Range(0, gif.Length - 8).Count(i => gif[i] == 0x21 && gif[i + 1] == 0xF9 && gif[i + 8] == 0x2C);

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }

    private static string Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        return Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
    }
}
