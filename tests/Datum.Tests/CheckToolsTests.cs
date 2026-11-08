using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class CheckToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-check-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;
    private string _part = "";

    public CheckToolsTests()
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
    public async Task MeasuresBetweenTwoEntitiesAndShowsOnlyWhatApplies()
    {
        await Part();
        var geometry = _backend.GeometryOf(_part);
        geometry.Faces.AddRange([new FaceInfo("face#1", SurfaceKind.Plane, 100, null, default, null, null), new FaceInfo("face#2", SurfaceKind.Plane, 100, null, default, null, null)]);
        geometry.Measured = new Measurement(10, 10, null, 0, new Point3(0, 0, 10), 10, null, null, null);

        var reply = Ok(await CheckTools.Measure(_connection, "face#1", "face#2"));

        Assert.Equal("face#1 to face#2: distance 10 mm · normal distance 10 mm · clearance 10 mm · angle 0° · delta 0,0,10 mm\n", reply);
    }

    [Fact]
    public async Task MeasuringAnUnknownHandleIsNotFound()
    {
        await Part();

        Assert.StartsWith("NOT_FOUND: ", Error(await CheckTools.Measure(_connection, "face#99")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadsAFinishedSketch()
    {
        await Part();
        var line = new SketchSegmentInfo("seg#1", "line", new Point2(0, 0), new Point2(10, 0), null, null, false);
        var circle = new SketchSegmentInfo("seg#2", "circle", default, default, new Point2(5, 5), 2, true);
        _backend.GeometryOf(_part).SketchDetails["feat#5"] = new SketchDetail(
            new SketchInfo("feat#5", "Sketch1", "under defined", 2, default, default, default),
            [line, circle],
            [new SketchRelationInfo("horizontal", ["seg#1"])],
            [new DimensionInfo("D1@Sketch1", 10, false, "feat#5")]);

        Assert.Equal(
            "feat#5 Sketch1: under defined, 2 items\n"
                + "seg#1 line 0,0 to 10,0\n"
                + "seg#2 circle centre 5,5 radius 2 (construction)\n"
                + "Relations:\nhorizontal: seg#1\n"
                + "Dimensions:\nD1@Sketch1 10 mm\n",
            Ok(await CheckTools.ReadSketch(_connection, "feat#5")));
    }

    [Fact]
    public async Task ReadingSomethingElseAsASketchIsRefused()
    {
        await Part();

        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await CheckTools.ReadSketch(_connection, "feat#1")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SummaryListsFeaturesAndProblemsWithoutTheDefaultPlanes()
    {
        await Part();
        var geometry = _backend.GeometryOf(_part);
        geometry.Model = new ModelInfo("Bracket", DocumentType.Part, null, 120, 44000, 9000, default, new Box(default, new Point3(60, 40, 5)), 1);
        geometry.Features.Clear();
        geometry.Features.AddRange(
        [
            new FeatureInfo("feat#1", "Front Plane", "RefPlane", 0, false, null),
            new FeatureInfo("feat#2", "Top Plane", "RefPlane", 0, false, null),
            new FeatureInfo("feat#3", "Right Plane", "RefPlane", 0, false, null),
            new FeatureInfo("feat#4", "Origin", "OriginProfileFeature", 0, false, null),
            new FeatureInfo("feat#5", "Boss-Extrude1", "Extrusion", 0, false, null),
            new FeatureInfo("feat#6", "Sketch1", "ProfileFeature", 1, false, null),
            new FeatureInfo("feat#7", "Fillet1", "Fillet", 0, false, "error swFeatureErrorFilletRadiusTooBig"),
        ]);
        geometry.Dimensions.Add(new DimensionInfo("D1@Boss-Extrude1", 5, false, "feat#5"));
        geometry.Facts = new ModelFacts("inches", "Default", 2, [new SketchInfo("feat#6", "Sketch1", "under defined", 4, default, default, default)]);

        Assert.Equal(
            "Bracket: 1 body, document units inches, configuration Default of 2\n"
                + "Material none · mass 120 g · volume 44000 mm³ · box 60 x 40 x 5 mm\n"
                + "Features (3):\n"
                + "feat#5 Boss-Extrude1 [Extrusion] D1=5 mm\n"
                + "  feat#6 Sketch1 [ProfileFeature] under defined\n"
                + "feat#7 Fillet1 [Fillet]\n"
                + "Problems (3):\n"
                + "- feat#7 Fillet1: FilletRadiusTooBig: the radius doesn't fit the geometry around the edge. Reduce the radius or enlarge the faces beside it\n"
                + "- feat#6 Sketch1 is under defined\n"
                + "- No material set, so the mass uses SOLIDWORKS's default density\n"
                + "Note: 3 of 3 features keep a default name\n",
            Ok(await CheckTools.Summary(_connection)));
    }

    [Fact]
    public async Task MaterialIsSetAndCanBeRestored()
    {
        await Part();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];

        var reply = Ok(await EditTools.Material(_connection, _workspace, "abs"));

        Assert.Contains("Material none → abs", reply, StringComparison.Ordinal);
        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));
        Assert.Null(_backend.GeometryOf(_part).Model!.Material);
    }

    [Fact]
    public async Task AnUnknownMaterialSuggestsTheClosestNames()
    {
        await Part();

        var error = Error(await EditTools.Material(_connection, _workspace, "stainless 316"));

        Assert.Equal("NOT_FOUND: no material named 'stainless 316'. Hint: Closest names: AISI 316 Stainless Steel Sheet", error);
    }

    [Fact]
    public async Task PropertiesAreSetListedAndRestored()
    {
        await Part();
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];

        Assert.Equal("PartNo: (not set) → 42\n", Ok(await EditTools.Property(_connection, _workspace, "PartNo", "42")));
        Assert.Equal("PartNo = 42\n", Ok(await EditTools.Properties(_connection)));

        Ok(await EditTools.Restore(_connection, _workspace, checkpoint));
        Assert.Equal("No custom properties\n", Ok(await EditTools.Properties(_connection)));
    }

    [Fact]
    public async Task APlaneNeedsExactlyOneWayToBePlaced()
    {
        await Part();

        var both = Error(await FeatureTools.Plane(_connection, _workspace, "Front", offset: 5, angle: 30, through: "edge#1"));
        var noEdge = Error(await FeatureTools.Plane(_connection, _workspace, "Front", angle: 30));

        Assert.StartsWith("INVALID_ARGUMENT: plane needs either offset", both, StringComparison.Ordinal);
        Assert.StartsWith("INVALID_ARGUMENT: plane needs through", noEdge, StringComparison.Ordinal);
        Assert.Contains(" is feat#", Ok(await FeatureTools.Plane(_connection, _workspace, "Front", offset: 5)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAxisFromAPlaneNeedsASecondPlane()
    {
        await Part();

        Assert.StartsWith("INVALID_ARGUMENT: axis needs a second plane", Error(await FeatureTools.Axis(_connection, _workspace, "Front")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReopeningASketchCanBeEndedButNotUndone()
    {
        await Part();
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        var sketch = Ok(await SketchTools.End(_connection, _workspace)).Split(' ')[2].TrimEnd(',');
        var features = _backend.GeometryOf(_part).Features.Count;
        var checkpoint = Ok(await EditTools.Create(_connection, "before")).Split(' ')[0];

        Assert.StartsWith("Editing Sketch1", Ok(await SketchTools.Edit(_connection, _workspace, sketch)), StringComparison.Ordinal);
        Assert.Contains("rebuild", Ok(await SketchTools.End(_connection, _workspace)), StringComparison.Ordinal);

        Assert.Equal(features, _backend.GeometryOf(_part).Features.Count);
        Assert.StartsWith("CANNOT_RESTORE: ", Error(await EditTools.Restore(_connection, _workspace, checkpoint)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnlyASketchCanBeReopened()
    {
        await Part();

        Assert.StartsWith("INVALID_ARGUMENT: feat#1 is not a sketch", Error(await SketchTools.Edit(_connection, _workspace, "feat#1")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckToolsRefuseADrawing()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "drawing"));

        Assert.StartsWith("INVALID_ARGUMENT: ", Error(await CheckTools.Summary(_connection)), StringComparison.Ordinal);
    }

    [Fact]
    public void TheFirstOfSeveralFailuresIsPointedOut()
    {
        FeatureInfo[] features =
        [
            new("feat#1", "Cut-Extrude1", "Cut", 0, false, "error swFeatureErrorCutNotIntersectModel"),
            new("feat#2", "Pattern", "LPattern", 0, false, "error swFeatureErrorSomethingNew"),
        ];

        var review = ModelProblems.Find(features, [], "Steel", 1);

        Assert.Equal(
            [
                "feat#1 Cut-Extrude1 (first failure; later ones may follow from it): CutNotIntersectModel: the cut doesn't touch the model. Check its direction, depth and sketch position",
                "feat#2 Pattern: SomethingNew: no known cause. Open the feature in SOLIDWORKS to see its message",
            ],
            review.Problems);
    }

    [Fact]
    public void ACleanPartHasNoProblems()
    {
        var review = ModelProblems.Find([new("feat#1", "Base plate", "Extrusion", 0, false, null)], [], "Steel", 1);

        Assert.Empty(review.Problems);
        Assert.Empty(review.Notes);
    }

    [Fact]
    public void SeveralBodiesAndSuppressionAreNotesNotProblems()
    {
        var review = ModelProblems.Find([new("feat#1", "Base plate", "Extrusion", 0, true, null)], [], "Steel", 2);

        Assert.Empty(review.Problems);
        Assert.Equal(["Suppressed: Base plate", "2 solid bodies; check that's intended"], review.Notes);
    }

    [Fact]
    public async Task MakeCheckReportsTheThinnestWallAndItsProblems()
    {
        await Part();
        _backend.GeometryOf(_part).Probes.AddRange(
        [
            new FaceProbe("face#1", SurfaceKind.Plane, default, new Point3(0, 0, 1), 10, false),
            new FaceProbe("face#2", SurfaceKind.Plane, default, new Point3(1, 0, 0), 0.5, false),
        ]);

        var report = Ok(await CheckTools.MakeCheck(_connection, "molding"));

        Assert.StartsWith("Part1 for molding: 2 faces, thinnest wall 0.5 mm\nProblems (2):\n", report, StringComparison.Ordinal);
        Assert.Contains("- face#2: wall 0.5 mm, under 1 mm\n", report, StringComparison.Ordinal);
        Assert.Contains("- face#2: draft 0.0°, under 1°\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MakeCheckRefusesSheetMetalOnAPlainPart()
    {
        await Part();

        Assert.StartsWith("INVALID_ARGUMENT: Part1 isn't sheet metal", Error(await CheckTools.MakeCheck(_connection, "sheetmetal")), StringComparison.Ordinal);
    }

    private async Task Part()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        _part = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        _backend.GeometryOf(_part).Features.Add(new FeatureInfo("feat#1", "Boss", "Extrusion", 0, false, null));
    }

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
