using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

// A 200 x 80 x 10 plate with two 10 mm holes 80 apart, and two 40 mm discs on 10 mm bores.
public sealed partial class MotionTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-motion-").FullName;
    private readonly ComBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public MotionTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(60));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
        Directory.CreateDirectory(Path.Combine(_root, "Datum"));
    }

    private string Plate => Path.Combine(_root, "Datum", "plate.sldprt");

    private string Disc => Path.Combine(_root, "Datum", "disc.sldprt");

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AGearedPairTurnsAtItsRatioAndExportsAGif() => await WithGearedDiscs(async (first, _) =>
    {
        var file = Path.Combine(_root, "Datum", "spin.gif");
        Ok(await MotionTools.New(_connection, _workspace, 2, "Spin"));
        Ok(await MotionTools.Motor(_connection, _workspace, "Spin", await Cylinder(first, 0), rpm: 60));
        var run = Ok(await MotionTools.Run(_connection, "Spin", samples: 8));
        var export = Ok(await MotionTools.Export(_connection, _workspace, "Spin", file, view: "top", fps: 5, width: 480));
        Console.WriteLine($"{run}{export}");

        Assert.Contains("0.25 s disc-1", run.Replace("<1>", "-1", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("2 s disc-1: turned 720°", run.Replace("turned -", "turned ", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("2 s disc-2: turned 1440°", run.Replace("turned -", "turned ", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.Contains("1 components didn't move", run, StringComparison.Ordinal);
        Assert.Equal("GIF89a", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(file), 0, 6));
        Assert.True(new FileInfo(file).Length > 2000);
    });

    [Fact]
    public async Task ASwingingMotorGoesOutAndBack() => await WithGearedDiscs(async (first, _) =>
    {
        Ok(await MotionTools.New(_connection, _workspace, 4, "Swing"));
        Ok(await MotionTools.Motor(_connection, _workspace, "Swing", await Cylinder(first, 0), swing: 30, period: 4));
        var run = Ok(await MotionTools.Run(_connection, "Swing", samples: 4));
        Console.WriteLine(run);

        var firstDisc = Regex.Matches(run, @"disc-1: turned (-?[\d.]+)").Select(m => Math.Abs(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))).ToArray();
        Assert.Equal(4, firstDisc.Length);
        Assert.InRange(firstDisc[1], 25, 31);
        Assert.InRange(firstDisc[3], 0, 5);
    });

    [Fact]
    public async Task AnMp4IsFinishedCentredAndAsLongAsTheStudy() => await WithGearedDiscs(async (first, _) =>
    {
        var file = Path.Combine(_root, "Datum", "spin.mp4");
        Ok(await MotionTools.New(_connection, _workspace, 2, "Spin"));
        Ok(await MotionTools.Motor(_connection, _workspace, "Spin", await Cylinder(first, 0), rpm: 30));
        var run = Ok(await MotionTools.Run(_connection, "Spin", times: [0.5, 1, 2]));
        var export = Ok(await MotionTools.Export(_connection, _workspace, "Spin", file, fps: 10, width: 640));
        Console.WriteLine($"{run}{export}");

        Assert.Contains("0.5 s disc-1: turned", run, StringComparison.Ordinal);
        Assert.Contains("It runs 2", export, StringComparison.Ordinal);
        Assert.DoesNotContain("WARNING", export, StringComparison.Ordinal);
        Assert.True(VideoFile.Read(file).Complete);
    });

    [Fact]
    public async Task ATurntableWritesAGifOfEveryFrame() => await WithGearedDiscs(async (_, _) =>
    {
        var file = Path.Combine(_root, "Datum", "turn.gif");

        Assert.StartsWith("Exported 8 frames", Ok(await MotionTools.Turntable(_connection, _workspace, file, seconds: 2, fps: 4, width: 320)), StringComparison.Ordinal);
    });

    [Fact]
    public async Task AStudyWithNoMotorSaysNothingMoved() => await WithGearedDiscs(async (_, _) =>
    {
        Ok(await MotionTools.New(_connection, _workspace, 1, "Still"));

        Assert.StartsWith("NOTHING MOVED.", Ok(await MotionTools.Run(_connection, "Still")), StringComparison.Ordinal);
    });

    [Fact]
    public async Task ALimitedAngleMateIsAccepted() => await WithGearedDiscs(async (first, _) =>
    {
        var reply = Ok(await AssemblyTools.Mate(_connection, _workspace, "angle", $"{first}:right", "Right", value: 45, min: 30, max: 60));
        Console.WriteLine(reply);

        Assert.Contains("Limit", Ok(await AssemblyTools.Mates(_connection)), StringComparison.OrdinalIgnoreCase);
    });

    private async Task<string> Cylinder(string component, int order) =>
        Ok(await InspectionTools.Faces(_connection, surface: "cylinder", component: component)).Split('\n')[order].Split(' ')[0];

    private async Task WithGearedDiscs(Func<string, string, Task> test)
    {
        await MakeParts();
        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));
        try
        {
            var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
            var first = Ok(await AssemblyTools.Add(_connection, _workspace, Disc, x: -40, z: 30)).Split(' ')[1];
            var second = Ok(await AssemblyTools.Add(_connection, _workspace, Disc, x: 40, z: 30)).Split(' ')[1];
            var holes = Ok(await InspectionTools.Faces(_connection, surface: "cylinder", component: plate)).Split('\n').Select(l => l.Split(' ')[0]).ToArray();
            Ok(await AssemblyTools.Mate(_connection, _workspace, "concentric", holes[0], await Cylinder(first, 1)));
            Ok(await AssemblyTools.Mate(_connection, _workspace, "concentric", holes[1], await Cylinder(second, 1)));
            var top = Ok(await InspectionTools.Faces(_connection, normal: "+z", component: plate, limit: 1)).Split(' ')[0];
            Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", top, Ok(await InspectionTools.Faces(_connection, normal: "-z", component: first, limit: 1)).Split(' ')[0]));
            Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", top, Ok(await InspectionTools.Faces(_connection, normal: "-z", component: second, limit: 1)).Split(' ')[0]));
            var gear = Ok(await AssemblyTools.Mate(_connection, _workspace, "gear", await Cylinder(first, 0), await Cylinder(second, 0), ratio: "2:1"));
            Console.WriteLine(gear);
            await test(first, second);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private async Task MakeParts()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace,
        [
            new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 200, Height = 80 },
            new SketchEntitySpec { Type = "circle", Cx = -40, Cy = 0, Radius = 5 },
            new SketchEntitySpec { Type = "circle", Cx = 40, Cy = 0, Radius = 5 },
        ]));
        Ok(await FeatureTools.Extrude(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), depth: 10));
        Ok(await DocumentTools.Save(_connection, _workspace, path: Plate));
        Ok(await DocumentTools.Close(_connection));

        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace,
        [
            new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 20 },
            new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 5 },
        ]));
        Ok(await FeatureTools.Extrude(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), depth: 10));
        Ok(await DocumentTools.Save(_connection, _workspace, path: Disc));
        Ok(await DocumentTools.Close(_connection));
    }

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();

    private static string Feat(string reply) => FeatHandle().Match(reply).Value;

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }
}
