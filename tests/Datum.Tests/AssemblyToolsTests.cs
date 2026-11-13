using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class AssemblyToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-assembly-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public AssemblyToolsTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(5));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task AddsAndListsAComponent()
    {
        await Assembly();

        var added = Ok(await AssemblyTools.Add(_connection, _workspace, "plate.sldprt", x: 10));

        Assert.Matches(@"^Added comp#\d+ plate-1 \[Default\] under defined, at 10,0,0 mm\n$", added);
        Assert.Equal(added["Added ".Length..], Ok(await AssemblyTools.List(_connection)));
    }

    [Fact]
    public async Task ComponentToolsNeedAnAssembly()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));

        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await AssemblyTools.List(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAssemblyOpenedFromDiskNeedsPermission()
    {
        var file = Path.Combine(_root, "Datum", "theirs.sldasm");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "assembly");
        Ok(await DocumentTools.Open(_connection, _workspace, file));

        Assert.StartsWith("NOT_OWNED: ", Error(await AssemblyTools.Add(_connection, _workspace, "plate.sldprt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MovingReportsThePoseAndCanBeUndone()
    {
        await Assembly();
        var plate = await Component();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];

        Assert.Equal($"{plate} moved at 5,6,7 mm, rotated 0,0,90°\n", Ok(await AssemblyTools.Move(_connection, _workspace, plate, 5, 6, 7, rz: 90)));

        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));
        Assert.Contains("at 0,0,0 mm\n", Ok(await AssemblyTools.List(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AHeldComponentSaysSo()
    {
        await Assembly();
        var plate = await Component();
        var id = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        _backend.GeometryOf(id).HeldComponents.Add(plate);

        Assert.Equal($"{plate} didn't reach the requested pose; mates hold it at 0,0,0 mm\n", Ok(await AssemblyTools.Move(_connection, _workspace, plate, 5, 0, 0)));
    }

    [Fact]
    public async Task AFixedComponentIsNotMoved()
    {
        await Assembly();
        var plate = await Component();
        Ok(await AssemblyTools.Fix(_connection, _workspace, plate));

        Assert.StartsWith($"INVALID_ARGUMENT: {plate} is fixed", Error(await AssemblyTools.Move(_connection, _workspace, plate, 5, 0, 0)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FixingAndAddingCanBeUndone()
    {
        await Assembly();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];
        var plate = await Component();
        Assert.Contains(" fixed, ", Ok(await AssemblyTools.Fix(_connection, _workspace, plate)), StringComparison.Ordinal);

        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));

        Assert.Equal("No components\n", Ok(await AssemblyTools.List(_connection)));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(90, 0, 0)]
    [InlineData(0, 90, 0)]
    [InlineData(0, 0, 90)]
    [InlineData(30, -45, 120)]
    public void PosesSurviveARoundTripThroughATransform(double rx, double ry, double rz)
    {
        var pose = new ComponentPose(new Point3(5, -6, 7), new Point3(rx, ry, rz));

        var back = ComponentPose.FromTransform(pose.ToTransform());

        Assert.True(pose.Matches(back), $"{pose} became {back}");
    }

    [Fact]
    public void RotatingAboutZTurnsXIntoY()
    {
        var pose = new ComponentPose(new Point3(1000, 0, 0), new Point3(0, 0, 90));

        var moved = pose.Place(new Point3(10, 0, 0));

        Assert.Equal(1000, moved.X, 6);
        Assert.Equal(10, moved.Y, 6);
        Assert.Equal(new Point3(0, 1, 0), pose.Turn(new Point3(1, 0, 0)) with { X = 0 });
    }

    [Fact]
    public void PosesThatDifferDoNotMatch()
    {
        var pose = new ComponentPose(new Point3(5, 0, 0), default);

        Assert.False(pose.Matches(new ComponentPose(new Point3(5.01, 0, 0), default)));
        Assert.False(pose.Matches(new ComponentPose(new Point3(5, 0, 0), new Point3(0, 0, 1))));
    }

    [Fact]
    public async Task AMateReportsWhatMovedAndWhatIsFree()
    {
        await Assembly();
        var plate = await Component();
        Geometry().NextMateMoves = [new ComponentMoved(plate, "plate-1", default, new ComponentPose(new Point3(0, 10, 0), default))];

        var reply = Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", "face#1", "face#2"));

        Assert.Matches(@"^Coincident1 is feat#\d+\n", reply);
        Assert.Contains($"{plate} plate-1 moved at 0,10,0 mm\n", reply, StringComparison.Ordinal);
        Assert.EndsWith("Still free to move: plate-1\n", reply, StringComparison.Ordinal);
        Assert.Contains("[coincident] face#1 to face#2", Ok(await AssemblyTools.Mates(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMateCanBeUndoneAndItsComponentsGoBack()
    {
        await Assembly();
        var plate = await Component();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];
        Geometry().NextMateMoves = [new ComponentMoved(plate, "plate-1", new ComponentPose(new Point3(1, 2, 3), default), default)];
        Ok(await AssemblyTools.Mate(_connection, _workspace, "lock", "face#1", "face#2"));

        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));

        Assert.Contains("at 1,2,3 mm", Ok(await AssemblyTools.List(_connection)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("glue", null, "closest", "INVALID_ARGUMENT: unknown mate 'glue'")]
    [InlineData("distance", null, "closest", "INVALID_ARGUMENT: a distance mate needs a value")]
    [InlineData("distance", -1d, "closest", "INVALID_ARGUMENT: a distance mate needs a value")]
    [InlineData("coincident", 5d, "closest", "INVALID_ARGUMENT: a coincident mate takes no value")]
    [InlineData("coincident", null, "sideways", "INVALID_ARGUMENT: unknown alignment 'sideways'")]
    [InlineData("screw", null, "closest", "INVALID_ARGUMENT: a screw mate needs its lead")]
    [InlineData("screw", 0d, "closest", "INVALID_ARGUMENT: a screw mate needs its lead")]
    public async Task BadMatesAreRefusedBeforeReachingSolidWorks(string kind, double? value, string alignment, string expected)
    {
        await Assembly();

        var reply = Error(await AssemblyTools.Mate(_connection, _workspace, kind, "face#1", "face#2", value, alignment));

        Assert.StartsWith(expected, reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailedMateIsReported()
    {
        await Assembly();
        _backend.FailNextFeature();

        Assert.StartsWith("FEATURE_FAILED: ", Error(await AssemblyTools.Mate(_connection, _workspace, "coincident", "face#1", "face#2")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FacesNeedAComponentInAnAssembly()
    {
        await Assembly();
        var plate = await Component();
        Geometry().Faces.Add(new FaceInfo("face#7", SurfaceKind.Plane, 100, null, default, null, null));

        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await InspectionTools.Faces(_connection)), StringComparison.Ordinal);
        Assert.StartsWith("face#7 ", Ok(await InspectionTools.Faces(_connection, component: plate)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheCheckListsEveryKindOfProblem()
    {
        await Assembly();
        var plate = await Component();
        Geometry().Interferences.Add(new InterferencePair(["pin-1", "plate-1"], 785.4));
        Geometry().Missing.Add(@"C:\gone\bolt.sldprt");
        Geometry().Mates.Add(new MateInfo("feat#9", "Distance1", "distance", ["a", "b"], false, "broken"));

        Assert.Equal(
            "Assem: 1 components, 1 mates, 1 overlaps at this pose\n"
                + "Problems (4):\n"
                + @"- Missing file: C:\gone\bolt.sldprt" + "\n"
                + "- pin-1 and plate-1 overlap by 785.4 mm³\n"
                + "- Distance1 has an error: broken\n"
                + "- No component is fixed, so the whole assembly can move\n"
                + "Free to move, as expected in a mechanism (1):\n"
                + "- plate-1 is under defined, so it can still move\n",
            Regex.Replace(Ok(await AssemblyTools.Check(_connection)), @"^Assembly\d+", "Assem"));
        Assert.NotEmpty(plate);
    }

    [Fact]
    public async Task TheCheckSaysWhenAMotionStudyIsShowing()
    {
        await Assembly();
        _backend.ShowingStudy = "LegOscillation";

        var report = Ok(await AssemblyTools.Check(_connection));

        Assert.EndsWith("Read while motion study 'LegOscillation' is showing, where SOLIDWORKS can call movable parts no solution. If that looks wrong, click the Model tab under the graphics area and check again\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACleanAssemblyHasNoProblems()
    {
        await Assembly();
        Ok(await AssemblyTools.Fix(_connection, _workspace, await Component()));

        Assert.EndsWith("No problems found\n", Ok(await AssemblyTools.Check(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheBomCountsRepeatedParts()
    {
        await Assembly();
        await Component();
        await Component();
        Ok(await AssemblyTools.Add(_connection, _workspace, "pin.sldprt"));

        Assert.Equal("2 x plate.sldprt [Default]\n1 x pin.sldprt [Default]\n", Ok(await AssemblyTools.Bom(_connection)));
    }

    [Fact]
    public async Task OpeningAnAssemblyReportsMissingFiles()
    {
        var file = Path.Combine(_root, "Datum", "theirs.sldasm");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "assembly");
        _backend.MissingOnOpen = [@"C:\gone\bolt.sldprt"];

        Assert.Contains(@"Missing file: C:\gone\bolt.sldprt", Ok(await DocumentTools.Open(_connection, _workspace, file)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SavingIsRefusedWhileAReferencedPartHasUnsavedChanges()
    {
        await Assembly();
        Geometry().UnsavedReferences.Add("pin.SLDPRT");

        Assert.StartsWith(
            "INVALID_ARGUMENT: pin.SLDPRT has unsaved changes",
            Error(await DocumentTools.Save(_connection, _workspace, path: "box.sldasm")),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSummaryCoversComponentsMatesAndProblems()
    {
        await Assembly();
        var plate = await Component();
        Ok(await AssemblyTools.Mate(_connection, _workspace, "coincident", "face#1", "face#2"));

        var summary = Ok(await CheckTools.Summary(_connection));

        Assert.Contains($"{plate} plate-1", summary, StringComparison.Ordinal);
        Assert.Contains("[coincident]", summary, StringComparison.Ordinal);
        Assert.Contains("- No component is fixed", summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGearMateShowsItsRatio()
    {
        await Assembly();
        var plate = await Component();
        Geometry().Mates.Add(new MateInfo("feat#9", "Gear1", "gear", ["a", "b"], false, null) { Ratio = "4:1 reversed" });

        Assert.Contains("[gear 4:1 reversed]", Ok(await CheckTools.Summary(_connection)), StringComparison.Ordinal);
        Assert.Contains("[gear 4:1 reversed]", Ok(await AssemblyTools.Mates(_connection)), StringComparison.Ordinal);
        Assert.NotEmpty(plate);
    }

    [Fact]
    public async Task AMateIsEditedWithTheFeatureTools()
    {
        await Assembly();
        var id = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        Geometry().Dimensions.Add(new DimensionInfo("D1@Distance1", 10, false, "feat#9"));

        Assert.StartsWith("D1@Distance1 10 → 25", Ok(await EditTools.SetDimension(_connection, _workspace, "D1@Distance1", 25, id)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAssemblyCanBeCaptured()
    {
        await Assembly();

        Assert.True((await ViewTools.Capture(_connection, _workspace)).IsError != true);
    }

    [Fact]
    public async Task ConfigurationsAreAddedActivatedAndListed()
    {
        await Assembly();

        Assert.Equal("Configuration Exploded added and active\n", Ok(await AssemblyTools.AddConfiguration(_connection, _workspace, "Exploded")));
        Assert.Equal("Default\nExploded (active)\n", Ok(await AssemblyTools.Configurations(_connection)));
        Assert.Equal("Configuration Default is active\n", Ok(await AssemblyTools.ActivateConfiguration(_connection, "Default")));
        Assert.StartsWith("INVALID_ARGUMENT: a configuration named Default", Error(await AssemblyTools.AddConfiguration(_connection, _workspace, "Default")), StringComparison.Ordinal);
        Assert.StartsWith("NOT_FOUND: no configuration named Nope", Error(await AssemblyTools.ActivateConfiguration(_connection, "Nope")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExplodedViewIsCreatedShownAndNotUndoable()
    {
        await Assembly();
        var plate = await Component();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];

        var reply = Ok(await AssemblyTools.Explode(_connection, _workspace, [new ExplodeStepSpec { Component = plate, Axis = "y", Distance = 50 }]));

        Assert.Equal("ExplView1 in configuration Default, 1 step\nStep1: plate-1 by 50 mm\n", reply);
        Assert.Equal("Exploded\n", Ok(await AssemblyTools.ShowExplode(_connection)));
        Assert.Equal("Collapsed\n", Ok(await AssemblyTools.ShowExplode(_connection, show: false)));
        Assert.StartsWith("INVALID_ARGUMENT: configuration Default already has", Error(await AssemblyTools.Explode(_connection, _workspace, [new ExplodeStepSpec { Component = plate, Axis = "y", Distance = 5 }])), StringComparison.Ordinal);
        Assert.StartsWith("CANNOT_RESTORE: ", Error(await EditTools.Restore(_connection, _workspace, checkpoint)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ShowingAnExplodeThatDoesNotExistIsNotFound()
    {
        await Assembly();

        Assert.StartsWith("NOT_FOUND: ", Error(await AssemblyTools.ShowExplode(_connection)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("face#1", "x", 5, "INVALID_ARGUMENT: 'face#1' is not a comp# handle")]
    [InlineData("comp#1", "up", 5, "INVALID_ARGUMENT: unknown axis 'up'")]
    [InlineData("comp#1", "x", 0, "INVALID_ARGUMENT: comp#1 needs a distance")]
    public async Task BadExplodeStepsAreRefused(string component, string axis, double distance, string expected)
    {
        await Assembly();

        var reply = Error(await AssemblyTools.Explode(_connection, _workspace, [new ExplodeStepSpec { Component = component, Axis = axis, Distance = distance }]));

        Assert.StartsWith(expected, reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExplodeRequirementsReadTheNamedConfiguration()
    {
        await Assembly();
        var plate = await Component();
        Ok(await AssemblyTools.AddConfiguration(_connection, _workspace, "Exploded"));
        Ok(await AssemblyTools.Explode(_connection, _workspace, [new ExplodeStepSpec { Component = plate, Axis = "y", Distance = 40 }]));
        Ok(await AssemblyTools.ActivateConfiguration(_connection, "Default"));
        Ok(await CheckTools.SetRequirements(_connection, _workspace,
        [
            new RequirementSpec { Id = "r1", Text = "Plate up", Check = "explode_step", Name = "Exploded", Expected = "plate-1", Target = 40 },
            new RequirementSpec { Id = "r2", Text = "Wrong distance", Check = "explode_step", Name = "Exploded", Expected = "plate-1", Target = 30 },
            new RequirementSpec { Id = "r3", Text = "Wrong part", Check = "explode_step", Name = "Exploded", Expected = "pin-1", Target = 40 },
            new RequirementSpec { Id = "r4", Text = "No such configuration", Check = "explode_step", Name = "Nope", Expected = "plate-1", Target = 40 },
            new RequirementSpec { Id = "r5", Text = "Default active", Check = "active_configuration", Expected = "Default" },
        ]));

        var report = Ok(await CheckTools.CheckRequirements(_connection, _workspace));

        Assert.Contains("PASS r1", report, StringComparison.Ordinal);
        Assert.Contains("FAIL r2", report, StringComparison.Ordinal);
        Assert.Contains("FAIL r3 Wrong part: no step moves pin-1", report, StringComparison.Ordinal);
        Assert.Contains("UNVERIFIED r4", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFileDatumSavedStaysEditableWhenReopened()
    {
        await Assembly();
        var file = Path.Combine(_root, "Datum", "mine.sldasm");
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
        File.WriteAllText(file, "assembly");

        Assert.DoesNotContain("not editable", Ok(await DocumentTools.Open(_connection, _workspace, file)), StringComparison.Ordinal);
        Assert.StartsWith("Added ", Ok(await AssemblyTools.Add(_connection, _workspace, "plate.sldprt")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AReadOnlyDocumentSaysSoAndClosesWithoutFuss()
    {
        await Assembly();
        var file = Path.Combine(_root, "Datum", "mine.sldasm");
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
        File.WriteAllText(file, "assembly");
        Ok(await DocumentTools.Open(_connection, _workspace, file, readOnly: true));

        Assert.Contains("is open read-only", Error(await AssemblyTools.Add(_connection, _workspace, "plate.sldprt")), StringComparison.Ordinal);
        var id = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        _backend.MarkModified(id);
        Ok(await DocumentTools.Close(_connection));
    }

    [Fact]
    public async Task OpeningWhatIsAlreadyOpenSaysSo()
    {
        await Assembly();
        var file = Path.Combine(_root, "Datum", "mine.sldasm");
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        File.WriteAllText(file, "assembly");

        Assert.Contains("Already open", Ok(await DocumentTools.Open(_connection, _workspace, file)), StringComparison.Ordinal);
    }

    private FakeGeometry Geometry() =>
        _backend.GeometryOf(_connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None).GetAwaiter().GetResult().Single(d => d.Active).Id);

    private async Task Assembly() => Ok(await DocumentTools.New(_connection, _workspace, "assembly"));

    private async Task<string> Component() => Ok(await AssemblyTools.Add(_connection, _workspace, "plate.sldprt")).Split(' ')[1];

    private static string Ok(CallToolResult result)
    {
        Assert.True(result.IsError != true, StatusToolsTests.TextOf(result));
        return StatusToolsTests.TextOf(result);
    }

    private static string Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        return StatusToolsTests.TextOf(result);
    }
}
