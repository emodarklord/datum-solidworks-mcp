using System.Globalization;
using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

// A 60 x 40 x 10 plate with a 10 mm hole at its centre, and a 10 mm diameter pin 30 long.
public sealed partial class AssemblyTests : IDisposable
{
    private static readonly string CaptureFolder = Path.Combine(Path.GetTempPath(), "datum-live-captures");

    private readonly string _root = Directory.CreateTempSubdirectory("datum-assembly-").FullName;
    private readonly ComBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public AssemblyTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(60));
        _workspace = new Workspace([Path.Combine(_root, "Datum"), CaptureFolder], Path.Combine(_root, "backups"));
    }

    private string Plate => Path.Combine(_root, "Datum", "plate.sldprt");

    private string Pin => Path.Combine(_root, "Datum", "pin.sldprt");

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AddsMovesAndRotatesComponentsWithoutTouchingTheFiles() => await InNewAssembly(async () =>
    {
        var stamp = File.GetLastWriteTimeUtc(Plate);

        var added = Ok(await AssemblyTools.Add(_connection, _workspace, Plate, x: 10, y: 20, z: 30));
        var handle = added.Split(' ')[1];
        Assert.Contains("at 10,20,30 mm", added, StringComparison.Ordinal);
        Ok(await AssemblyTools.Fix(_connection, _workspace, handle, isFixed: false));
        var moved = Ok(await AssemblyTools.Move(_connection, _workspace, handle, 100, 50, 0, rz: 90));
        var list = Ok(await AssemblyTools.List(_connection));
        Console.WriteLine($"{added}{moved}{list}");

        Assert.Contains("moved at 100,50,0 mm, rotated 0,0,90°", moved, StringComparison.Ordinal);
        Assert.Contains("plate-1", list, StringComparison.Ordinal);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Plate));
        Assert.DoesNotContain("plate", await OpenTitles(), StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public async Task AddingTheSameFileTwiceGivesTwoComponents() => await InNewAssembly(async () =>
    {
        Ok(await AssemblyTools.Add(_connection, _workspace, Plate));
        Ok(await AssemblyTools.Add(_connection, _workspace, Plate, x: 100));

        var list = Ok(await AssemblyTools.List(_connection));

        Assert.Contains("plate-1", list, StringComparison.Ordinal);
        Assert.Contains("plate-2", list, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AFixedComponentDoesNotMoveAndUndoWorks() => await InNewAssembly(async () =>
    {
        var checkpoint = Ok(await EditTools.Create(_connection, "start")).Split(' ')[0];
        var handle = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];

        var moved = Text(await AssemblyTools.Move(_connection, _workspace, handle, 50, 0, 0));

        Assert.StartsWith($"INVALID_ARGUMENT: {handle} is fixed", moved, StringComparison.Ordinal);
        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));
        Assert.Equal("No components\n", Ok(await AssemblyTools.List(_connection)));
    });

    [Fact]
    public async Task AMissingFileIsRefused() => await InNewAssembly(async () =>
    {
        var reply = Text(await AssemblyTools.Add(_connection, _workspace, Path.Combine(_root, "Datum", "nothing.sldprt")));

        Assert.StartsWith("NOT_FOUND: ", reply, StringComparison.Ordinal);
    });

    [Fact]
    public async Task APinSeatsInAHoleAndAMeasureConfirmsIt() => await InNewAssembly(async () =>
    {
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        var hole = await FaceOf(plate, surface: "cylinder");
        var shaft = await FaceOf(pin, surface: "cylinder");

        var concentric = Ok(await AssemblyTools.Mate(_connection, _workspace, "concentric", hole, shaft));
        var seated = Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", await FaceOf(plate, normal: "+y"), await FaceOf(pin, normal: "-y")));
        var turned = Ok(await AssemblyTools.Mate(_connection, _workspace, "parallel", $"{plate}:front", $"{pin}:front"));
        var pinTop = await FaceOf(pin, normal: "+y");
        var height = Ok(await CheckTools.Measure(_connection, pinTop, await FaceOf(plate, normal: "+y")));
        var list = Ok(await AssemblyTools.List(_connection));
        Console.WriteLine($"{concentric}{seated}{turned}{height}{list}{Ok(await AssemblyTools.Mates(_connection))}");

        Assert.Contains("normal distance 30 mm", height, StringComparison.Ordinal);
        Assert.Contains("All components are fixed or fully defined", turned, StringComparison.Ordinal);
        Assert.Contains("at 0,10,0 mm", list, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AProfileCenterMateCentresThePinOnThePlate() => await InNewAssembly(async () =>
    {
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100, z: 40)).Split(' ')[1];
        Ok(await AssemblyTools.Fix(_connection, _workspace, plate));

        var mate = Ok(await AssemblyTools.Mate(_connection, _workspace, "profile_center", await FaceOf(plate, normal: "+y"), await FaceOf(pin, normal: "-y"), lockRotation: true));
        var list = Ok(await AssemblyTools.List(_connection));
        Console.WriteLine($"{mate}{list}{Ok(await AssemblyTools.Mates(_connection))}");

        Assert.Contains($"{pin} pin-1 [Default] fully defined, at 0,10,0 mm", list, StringComparison.Ordinal);
        Assert.Contains("[profilecenter]", Ok(await AssemblyTools.Mates(_connection)), StringComparison.Ordinal);
    });

    [Fact]
    public async Task ASubAssemblyCanBeMadeFlexibleAndRigidAgain() => await InNewAssembly(async () =>
    {
        var main = (await Assembly()).Id;
        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));
        Ok(await AssemblyTools.Add(_connection, _workspace, Plate));
        Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100));
        Ok(await DocumentTools.Save(_connection, _workspace, path: Path.Combine(_root, "Datum", "pinned.sldasm")));
        Ok(await DocumentTools.Close(_connection));
        Ok(await DocumentTools.Activate(_connection, _workspace, main));
        var sub = Ok(await AssemblyTools.Add(_connection, _workspace, Path.Combine(_root, "Datum", "pinned.sldasm"))).Split(' ')[1];

        var inside = Ok(await InspectionTools.Faces(_connection, component: sub, surface: "cylinder"));
        Assert.Contains("radius 5 mm", inside, StringComparison.Ordinal);
        var flexible = Ok(await AssemblyTools.Flexible(_connection, _workspace, sub));
        var rigid = Ok(await AssemblyTools.Flexible(_connection, _workspace, sub, flexible: false));
        var part = Error(await AssemblyTools.Flexible(_connection, _workspace, Ok(await AssemblyTools.Add(_connection, _workspace, Pin)).Split(' ')[1]));
        Console.WriteLine($"{flexible}{rigid}{part}");

        Assert.Contains(", flexible, at", flexible, StringComparison.Ordinal);
        Assert.DoesNotContain("flexible", rigid, StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: ", part, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AScrewMateTurnsThePartAsItTravels() => await InNewAssembly(async () =>
    {
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        var hole = await FaceOf(plate, surface: "cylinder");
        var shaft = await FaceOf(pin, surface: "cylinder");

        Ok(await AssemblyTools.Mate(_connection, _workspace, "concentric", hole, shaft));
        Ok(await AssemblyTools.Mate(_connection, _workspace, "screw", hole, shaft, value: 4));
        var mates = Ok(await AssemblyTools.Mates(_connection));
        Ok(await MotionTools.New(_connection, _workspace, 2, "Drive"));
        Ok(await MotionTools.Motor(_connection, _workspace, "Drive", shaft, rpm: 60));
        var run = Ok(await MotionTools.Run(_connection, "Drive", samples: 4));
        Console.WriteLine($"{mates}{run}");

        Assert.Contains("[screw 4 mm/turn]", mates, StringComparison.Ordinal);
        var travel = Regex.Matches(run, @"moved ([\d.]+) mm").Select(m => double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
        Assert.Equal(4, travel.Length);
        Assert.All(travel.Zip(travel.Skip(1)), step => Assert.Equal(2, Math.Abs(step.First - step.Second), 2));
    });

    [Fact]
    public async Task AMateCanBeChangedSuppressedAndDeleted() => await InNewAssembly(async () =>
    {
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        var hole = await FaceOf(plate, surface: "cylinder");
        var shaft = await FaceOf(pin, surface: "cylinder");
        Ok(await AssemblyTools.Mate(_connection, _workspace, "concentric", hole, shaft));
        var gap = Ok(await AssemblyTools.Mate(_connection, _workspace, "distance", await FaceOf(plate, normal: "+y"), await FaceOf(pin, normal: "-y"), value: 10)).Split(default(char[]), StringSplitOptions.RemoveEmptyEntries)[2];
        var name = Ok(await EditTools.Dimensions(_connection)).Split('\n').Single(l => l.StartsWith("D1@Distance", StringComparison.Ordinal)).Split(' ')[0];

        var changed = Ok(await EditTools.SetDimension(_connection, _workspace, name, 25));
        var height = Ok(await CheckTools.Measure(_connection, await FaceOf(plate, normal: "+y"), await FaceOf(pin, normal: "-y")));
        var suppressed = Ok(await EditTools.Suppress(_connection, _workspace, [gap]));
        var mates = Ok(await AssemblyTools.Mates(_connection));
        Ok(await EditTools.Suppress(_connection, _workspace, [gap], suppress: false));
        var deleted = Ok(await EditTools.Delete(_connection, _workspace, [gap]));
        var left = Ok(await AssemblyTools.Mates(_connection));
        Console.WriteLine($"{changed}{height}{suppressed}{mates}{deleted}{left}");

        Assert.Contains("distance 25 mm", height, StringComparison.Ordinal);
        Assert.Contains("suppressed", mates, StringComparison.Ordinal);
        Assert.DoesNotContain("[distance]", left, StringComparison.Ordinal);
    });

    [Fact]
    public async Task TwoInstancesKeepSeparateFaces() => await InNewAssembly(async () =>
    {
        var first = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var second = Ok(await AssemblyTools.Add(_connection, _workspace, Plate, x: 100, y: 5)).Split(' ')[1];

        var a = await FaceOf(first, normal: "+y");
        var b = await FaceOf(second, normal: "+y");
        var between = Ok(await CheckTools.Measure(_connection, a, b));
        var centre = Ok(await InspectionTools.Faces(_connection, component: second, normal: "+y"));
        Console.WriteLine($"{between}{centre}");

        Assert.NotEqual(a, b);
        Assert.Contains("normal distance 5 mm", between, StringComparison.Ordinal);
        Assert.Contains("100,15,0", centre, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AnImpossibleMateRollsBackAndLeavesEverythingInPlace() => await InNewAssembly(async () =>
    {
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", await FaceOf(plate, normal: "+y"), await FaceOf(pin, normal: "-y")));
        var before = Ok(await AssemblyTools.List(_connection));

        var reply = Text(await AssemblyTools.Mate(_connection, _workspace, "coincident", await FaceOf(plate, normal: "-y"), await FaceOf(pin, normal: "-y")));
        Console.WriteLine(reply);

        Assert.StartsWith("FEATURE_FAILED: ", reply, StringComparison.Ordinal);
        Assert.Equal(before, Ok(await AssemblyTools.List(_connection)));
        Assert.Single(Ok(await AssemblyTools.Mates(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries));
    });

    [Fact]
    public async Task AMatedComponentIsHeldAndUndoLetsItGo() => await InNewAssembly(async () =>
    {
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];
        var mated = Ok(await AssemblyTools.Mate(_connection, _workspace, "lock", plate, pin));

        var held = Ok(await AssemblyTools.Move(_connection, _workspace, pin, 200, 0, 0));
        Console.WriteLine($"{mated}{held}");
        Assert.Contains("mates hold it", held, StringComparison.Ordinal);

        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));
        Assert.Equal("No mates\n", Ok(await AssemblyTools.Mates(_connection)));
        Assert.Contains("at 100,0,0 mm", Ok(await AssemblyTools.List(_connection)), StringComparison.Ordinal);
    });

    [Fact]
    public async Task AClashReportsItsVolumeAndMovingClearGivesNone() => await InNewAssembly(async () =>
    {
        // The pin passes through the plate's 10 mm thickness away from the hole.
        Ok(await AssemblyTools.Add(_connection, _workspace, Plate));
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 20, y: -5)).Split(' ')[1];

        var clash = Ok(await AssemblyTools.Check(_connection));
        Console.WriteLine(clash);
        var overlap = double.Parse(OverlapVolume().Match(clash).Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal(Math.PI * 25 * 10, overlap, 1);

        Ok(await AssemblyTools.Move(_connection, _workspace, pin, 100, 0, 0));
        Assert.Contains("0 overlaps at this pose", Ok(await AssemblyTools.Check(_connection)), StringComparison.Ordinal);
    });

    [Fact]
    public async Task TheBomCountsThreePinsAndReadsProperties() => await InNewAssembly(async () =>
    {
        Ok(await DocumentTools.Open(_connection, _workspace, Pin));
        Ok(await DocumentTools.AllowEdit(_connection, _workspace, Pin));
        Ok(await EditTools.Property(_connection, _workspace, "PartNo", "P-100"));
        Ok(await DocumentTools.Save(_connection, _workspace));
        Ok(await DocumentTools.Close(_connection));
        Ok(await DocumentTools.Activate(_connection, _workspace, (await Assembly()).Id));
        Ok(await AssemblyTools.Add(_connection, _workspace, Plate));
        for (var x = 100; x <= 300; x += 100)
        {
            Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: x));
        }

        var bom = Ok(await AssemblyTools.Bom(_connection));
        Console.WriteLine(bom);

        Assert.Contains("3 x pin.SLDPRT [Default] PartNo P-100", bom, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("1 x plate.SLDPRT", bom, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public async Task AMissingPartIsReportedOnOpenAndByTheCheck() => await InNewAssembly(async () =>
    {
        var spare = Path.Combine(_root, "Datum", "spare.sldprt");
        File.Copy(Pin, spare);
        var file = Path.Combine(_root, "Datum", "with-spare.sldasm");
        Ok(await AssemblyTools.Add(_connection, _workspace, Plate));
        Ok(await AssemblyTools.Add(_connection, _workspace, spare, x: 100));
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
        File.Delete(spare);

        var opened = Ok(await DocumentTools.Open(_connection, _workspace, file));
        var check = Ok(await AssemblyTools.Check(_connection));
        Console.WriteLine($"{opened}{check}");

        Assert.Contains("Missing file: ", opened, StringComparison.Ordinal);
        Assert.Contains("spare", check, StringComparison.OrdinalIgnoreCase);
    });

    [Fact]
    public async Task SummaryPicturesAndRequirementsWorkOnARealAssembly() => await InNewAssembly(async () =>
    {
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        Ok(await AssemblyTools.Mate(_connection, _workspace, "concentric", await FaceOf(plate, surface: "cylinder"), await FaceOf(pin, surface: "cylinder")));
        Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", await FaceOf(plate, normal: "+y"), await FaceOf(pin, normal: "-y")));
        Ok(await AssemblyTools.Mate(_connection, _workspace, "parallel", $"{plate}:front", $"{pin}:front"));
        Ok(await CheckTools.SetRequirements(_connection, _workspace,
        [
            new RequirementSpec { Id = "r1", Text = "Height", Check = "box_y", Target = 40 },
            new RequirementSpec { Id = "r2", Text = "No clashes", Check = "no_interference" },
            new RequirementSpec { Id = "r3", Text = "Fully mated", Check = "fully_mated" },
            new RequirementSpec { Id = "r4", Text = "One pin", Check = "bom_quantity", Name = "pin", Target = 1 },
            new RequirementSpec { Id = "r5", Text = "Seat height", Check = "measure", First = await FaceOf(pin, normal: "+y"), Second = "Top", Quantity = "normal_distance", Target = 40 },
        ]));

        var summary = Ok(await CheckTools.Summary(_connection));
        var report = Ok(await CheckTools.CheckRequirements(_connection, _workspace));
        var picture = await ViewTools.Capture(_connection, _workspace, "iso", path: Path.Combine(CaptureFolder, "assembly-iso.png"), overwrite: true, labels: "faces");
        var section = await ViewTools.Capture(_connection, _workspace, "iso", path: Path.Combine(CaptureFolder, "assembly-section.png"), overwrite: true, section: "right");
        Console.WriteLine($"{summary}{report}{Assert.IsType<TextContentBlock>(picture.Content[0]).Text}");

        Assert.Contains("box 60 x 40 x 40 mm", summary, StringComparison.Ordinal);
        Assert.Contains("No problems found", summary, StringComparison.Ordinal);
        Assert.Contains(": 5 pass, 0 fail", report, StringComparison.Ordinal);
        Assert.True(picture.IsError != true);
        Assert.Contains("numbers are face#", Assert.IsType<TextContentBlock>(picture.Content[0]).Text, StringComparison.Ordinal);
        Assert.True(section.IsError != true);
    });

    [Fact]
    public async Task HandlesKeepWorkingAcrossConfigurationsAndAReopen() => await InNewAssembly(async () =>
    {
        var file = Path.Combine(_root, "Datum", "pair.sldasm");
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        var face = await FaceOf(plate, normal: "+y");

        Ok(await AssemblyTools.AddConfiguration(_connection, _workspace, "Other"));
        Ok(await AssemblyTools.ActivateConfiguration(_connection, "Default"));
        var list = Ok(await AssemblyTools.Configurations(_connection));
        var moved = Ok(await AssemblyTools.Move(_connection, _workspace, pin, 120, 0, 0));
        var measured = Ok(await CheckTools.Measure(_connection, face));
        Console.WriteLine($"{list}{moved}{measured}");

        Assert.Contains("Default (active)", list, StringComparison.Ordinal);
        Assert.Contains("moved at 120,0,0 mm", moved, StringComparison.Ordinal);
        Assert.Contains("area", measured, StringComparison.Ordinal);

        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
        Ok(await DocumentTools.Open(_connection, _workspace, file));
        var reopened = Ok(await AssemblyTools.List(_connection));
        Console.WriteLine(reopened);
        Assert.Contains("at 120,0,0 mm", reopened, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AnExplodedViewMovesAComponentAndCollapsesBackExactly() => await InNewAssembly(async () =>
    {
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        Ok(await AssemblyTools.Mate(_connection, _workspace, "concentric", await FaceOf(plate, surface: "cylinder"), await FaceOf(pin, surface: "cylinder")));
        Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", await FaceOf(plate, normal: "+y"), await FaceOf(pin, normal: "-y")));
        Ok(await AssemblyTools.Mate(_connection, _workspace, "parallel", $"{plate}:front", $"{pin}:front"));
        Ok(await AssemblyTools.AddConfiguration(_connection, _workspace, "Exploded"));
        var collapsed = await ViewTools.Capture(_connection, _workspace, "front", path: Path.Combine(CaptureFolder, "explode-before.png"), overwrite: true);

        var created = Ok(await AssemblyTools.Explode(_connection, _workspace, [new ExplodeStepSpec { Component = pin, Axis = "y", Distance = 50 }]));
        var shown = Ok(await AssemblyTools.ShowExplode(_connection));
        var exploded = await ViewTools.Capture(_connection, _workspace, "front", path: Path.Combine(CaptureFolder, "explode-after.png"), overwrite: true);
        var back = Ok(await AssemblyTools.ShowExplode(_connection, show: false));
        var again = await ViewTools.Capture(_connection, _workspace, "front", path: Path.Combine(CaptureFolder, "explode-back.png"), overwrite: true);
        var list = Ok(await AssemblyTools.List(_connection));
        Console.WriteLine($"{created}{shown}{back}{list}");

        Assert.Contains("by 50 mm", created, StringComparison.Ordinal);
        Assert.Equal("Exploded\n", shown);
        Assert.Equal("Collapsed\n", back);
        Assert.Contains("at 0,10,0 mm", list, StringComparison.Ordinal);
        Assert.NotEqual(PngBytes(collapsed), PngBytes(exploded));
        Assert.Equal(PngBytes(collapsed), PngBytes(again));
        Ok(await AssemblyTools.ActivateConfiguration(_connection, "Default"));
        Assert.StartsWith("NOT_FOUND: ", Text(await AssemblyTools.ShowExplode(_connection)), StringComparison.Ordinal);
    });

    private static string PngBytes(CallToolResult capture) =>
        Convert.ToHexString(Assert.IsType<ImageContentBlock>(capture.Content[1]).DecodedData.ToArray());

    [Fact]
    public async Task MeasuresUseAssemblyCoordinatesNotThePartsOwn() => await InNewAssembly(async () =>
    {
        // The plate sits 30 along X and 7 along Z, so a reading in a part's own coordinates comes out wrong.
        var plate = Ok(await AssemblyTools.Add(_connection, _workspace, Plate, x: 30, z: 7)).Split(' ')[1];
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        var hole = await FaceOf(plate, surface: "cylinder");
        var shaft = await FaceOf(pin, surface: "cylinder");
        Ok(await AssemblyTools.Mate(_connection, _workspace, "concentric", hole, shaft));
        Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", await FaceOf(plate, normal: "-y"), await FaceOf(pin, normal: "-y")));
        var pinTop = await FaceOf(pin, normal: "+y");

        var holeToRight = Ok(await CheckTools.Measure(_connection, hole, "Right"));
        var topToTop = Ok(await CheckTools.Measure(_connection, pinTop, "Top"));
        var pinToPlate = Ok(await CheckTools.Measure(_connection, shaft, $"{plate}:right"));
        var axes = Ok(await CheckTools.Measure(_connection, hole, shaft));
        var flush = Ok(await CheckTools.Measure(_connection, await FaceOf(plate, normal: "-y"), await FaceOf(pin, normal: "-y")));
        Console.WriteLine($"{holeToRight}{topToTop}{pinToPlate}{axes}{flush}");

        Assert.Contains("plane offset 30 mm", holeToRight, StringComparison.Ordinal);
        Assert.Contains("plane offset 30 mm", topToTop, StringComparison.Ordinal);
        Assert.Contains("plane offset 0 mm", pinToPlate, StringComparison.Ordinal);
        Assert.Contains("axis distance 0 mm", axes, StringComparison.Ordinal);
        Assert.Contains("normal distance 0 mm", flush, StringComparison.Ordinal);
    });

    [Fact]
    public async Task ExplodeRequirementsReadBackAndShowingWorksReadOnly() => await InNewAssembly(async () =>
    {
        var file = Path.Combine(_root, "Datum", "exploding.sldasm");
        Ok(await AssemblyTools.Add(_connection, _workspace, Plate));
        var pin = Ok(await AssemblyTools.Add(_connection, _workspace, Pin, x: 100)).Split(' ')[1];
        Ok(await AssemblyTools.AddConfiguration(_connection, _workspace, "Exploded"));
        Ok(await AssemblyTools.Explode(_connection, _workspace, [new ExplodeStepSpec { Component = pin, Axis = "y", Distance = 20 }]));
        Ok(await AssemblyTools.ActivateConfiguration(_connection, "Default"));
        Ok(await CheckTools.SetRequirements(_connection, _workspace,
        [
            new RequirementSpec { Id = "r1", Text = "Pin up 20", Check = "explode_step", Name = "Exploded", Expected = "pin-1", Target = 20 },
            new RequirementSpec { Id = "r2", Text = "Default active", Check = "active_configuration", Expected = "Default" },
        ]));
        var report = Ok(await CheckTools.CheckRequirements(_connection, _workspace));
        Console.WriteLine(report);
        Assert.Contains(": 2 pass, 0 fail", report, StringComparison.Ordinal);

        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
        var opened = Ok(await DocumentTools.Open(_connection, _workspace, file));
        Assert.DoesNotContain("not editable", opened, StringComparison.Ordinal);
        Ok(await DocumentTools.Close(_connection, discardChanges: true));

        Ok(await DocumentTools.Open(_connection, _workspace, file, readOnly: true));
        Ok(await AssemblyTools.ActivateConfiguration(_connection, "Exploded"));
        Assert.Equal("Exploded\n", Ok(await AssemblyTools.ShowExplode(_connection)));
        Ok(await DocumentTools.Close(_connection));
        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));
    });

    private async Task<DocumentInfo> Assembly() =>
        (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).First(d => d.Type == DocumentType.Assembly);

    private async Task<string> FaceOf(string component, string? surface = null, string? normal = null) =>
        Ok(await InspectionTools.Faces(_connection, component: component, surface: surface, normal: normal)).Split(' ')[0];

    private async Task<string> OpenTitles() => Ok(await DocumentTools.List(_connection, _workspace, CancellationToken.None));

    private async Task InNewAssembly(Func<Task> test)
    {
        await MakePart(Plate, "Top", [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 60, Height = 40, Dimension = true }, new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 5, Dimension = true }], 10);
        await MakePart(Pin, "Top", [new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 5, Dimension = true }], 30);
        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));
        try
        {
            await test();
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private async Task MakePart(string file, string plane, SketchEntitySpec[] profile, double depth)
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, plane));
        Ok(await SketchTools.Add(_connection, _workspace, profile));
        var sketch = FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value;
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: depth));
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
    }

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static string Ok(CallToolResult result)
    {
        var text = Text(result);
        Assert.True(result.IsError != true, text);
        return text;
    }

    private static string Error(CallToolResult result)
    {
        var text = Text(result);
        Assert.True(result.IsError, text);
        return text;
    }

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();

    [GeneratedRegex(@"overlap by ([\d.]+) mm")]
    private static partial Regex OverlapVolume();
}
