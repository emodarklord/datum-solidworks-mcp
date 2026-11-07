using System.Globalization;
using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

// An 80 x 50 x 20 block with a 12 mm through hole at x = 20, drawn at 1:1 unless a test says otherwise.
public sealed partial class DrawingTests : IDisposable
{
    private static readonly string CaptureFolder = Path.Combine(Path.GetTempPath(), "datum-live-captures");

    private readonly string _root = Directory.CreateTempSubdirectory("datum-drawing-").FullName;
    private readonly ComBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public DrawingTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(60));
        _workspace = new Workspace([Path.Combine(_root, "Datum"), CaptureFolder], Path.Combine(_root, "backups"));
    }

    private string Block => Path.Combine(_root, "Datum", "block.sldprt");

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task MakesASheetWithStandardViewsAndATitleBlock() => await InDrawing(async () =>
    {
        var picture = await ViewTools.Capture(_connection, _workspace, size: 1200, path: Path.Combine(CaptureFolder, "drawing-standard.png"), overwrite: true);
        var list = Ok(await DrawingTools.ListViews(_connection));
        Console.WriteLine(list);

        Assert.True(picture.IsError != true);
        Assert.Equal(4, list.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("1:2", list, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AViewCanBeMovedRescaledAndKeepsItAfterAReopen() => await InDrawing(async () =>
    {
        var file = Path.Combine(_root, "Datum", "moved.slddrw");
        var view = Handle(Ok(await DrawingTools.ListViews(_connection)));

        var changed = Ok(await DrawingTools.SetView(_connection, _workspace, view, x: 90, y: 70, scale: "1:4", display: "hidden"));
        Console.WriteLine(changed);
        Assert.Contains("1:4 at 90,70 mm", changed, StringComparison.Ordinal);
        Assert.Contains(", hidden, ", changed, StringComparison.Ordinal);

        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
        Ok(await DocumentTools.Open(_connection, _workspace, file));
        var reopened = Ok(await DrawingTools.ListViews(_connection));
        Console.WriteLine(reopened);

        Assert.Contains("1:4 at 90,70 mm", reopened, StringComparison.Ordinal);
        Assert.Contains(", hidden, ", reopened, StringComparison.Ordinal);
    });

    [Fact]
    public async Task ClosingADrawingReallyClosesItAndReleasesItsModel() => await InDrawing(async () =>
    {
        var file = Path.Combine(_root, "Datum", "closing.slddrw");
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));

        Assert.DoesNotContain("closing", Ok(await DocumentTools.List(_connection, _workspace, CancellationToken.None)), StringComparison.OrdinalIgnoreCase);
        using var stream = new FileStream(Block, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Ok(await DrawingTools.New(_connection, _workspace));
    });

    [Fact]
    public async Task ASectionThroughTheHoleShowsIt() => await InDrawing(async () =>
    {
        var front = Handle(Ok(await DrawingTools.ListViews(_connection)));

        var section = Ok(await DrawingTools.Section(_connection, _workspace, front, "vertical", 20, 130, 60, label: "A"));
        var handle = Handle(section);
        var edges = Ok(await DrawingTools.Entities(_connection, handle));
        var picture = await ViewTools.Capture(_connection, _workspace, size: 1400, path: Path.Combine(CaptureFolder, "drawing-section.png"), overwrite: true);
        var pdf = Path.Combine(CaptureFolder, "drawing-section.pdf");
        Ok(await DocumentTools.Export(_connection, _workspace, pdf, overwrite: true));
        Console.WriteLine($"{section}{edges}");

        Assert.Contains("[section]", section, StringComparison.Ordinal);
        Assert.True(picture.IsError != true);
        Assert.True(edges.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length > 4, edges);
    });

    [Fact]
    public async Task ADetailEnlargesThePartOfTheViewAskedFor() => await InDrawing(async () =>
    {
        var front = Handle(Ok(await DrawingTools.ListViews(_connection)));

        var detail = Ok(await DrawingTools.Detail(_connection, _workspace, front, 20, 0, 12, "2:1", 120, 60, label: "B"));
        Console.WriteLine(detail);

        Assert.Contains("[detail] 2:1", detail, StringComparison.Ordinal);
    });

    [Fact]
    public async Task EntitiesAreListedInSheetCoordinates() => await InDrawing(async () =>
    {
        var front = Handle(Ok(await DrawingTools.ListViews(_connection)));

        var entities = Ok(await DrawingTools.Entities(_connection, front));
        var view = Ok(await DrawingTools.ListViews(_connection)).Split('\n')[0];
        Console.WriteLine($"{view}\n{entities}");

        Assert.Contains(" circle ", entities, StringComparison.Ordinal);
        Assert.Equal(4, Regex.Count(entities, " line "));
    });

    [Fact]
    public async Task AnAuxiliaryViewProjectsFromAnEdge() => await InDrawing(async () =>
    {
        var front = Handle(Ok(await DrawingTools.ListViews(_connection)));
        var edge = Ok(await DrawingTools.Entities(_connection, front)).Split('\n').First(line => line.Contains(" line ", StringComparison.Ordinal)).Split(' ')[0];

        var auxiliary = Ok(await DrawingTools.Auxiliary(_connection, _workspace, front, edge, 200, 60));
        Console.WriteLine(auxiliary);

        Assert.Contains("[auxiliary]", auxiliary, StringComparison.Ordinal);
    });

    [Fact]
    public async Task ImportsDimensionsTolerancesThemAndMarksTheHole() => await InDrawing(async () =>
    {
        var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).ToArray();

        var imported = Ok(await DrawingTools.ImportDimensions(_connection, _workspace));
        var list = Ok(await DrawingTools.ListDimensions(_connection));
        var name = Regex.Match(list, @"(D\d+@Sketch1) 80 mm").Groups[1].Value;
        var tolerance = Ok(await DrawingTools.Tolerance(_connection, _workspace, name, "bilateral", 0.2, -0.1));
        var side = Ok(await DrawingTools.Marks(_connection, _workspace, views[2]));
        var front = Ok(await DrawingTools.Marks(_connection, _workspace, views[0]));
        var note = Ok(await DrawingTools.Note(_connection, _workspace, "BREAK ALL SHARP EDGES", 30, 30));
        var after = Ok(await DrawingTools.ListDimensions(_connection));
        var picture = await ViewTools.Capture(_connection, _workspace, size: 1400, path: Path.Combine(CaptureFolder, "drawing-dimensions.png"), overwrite: true);
        Ok(await DocumentTools.Export(_connection, _workspace, Path.Combine(CaptureFolder, "drawing-dimensions.pdf"), overwrite: true));
        Console.WriteLine($"{imported}{list}{tolerance}{side}{front}{note}{after}");

        Assert.Contains("80 mm", list, StringComparison.Ordinal);
        Assert.Contains("50 mm", list, StringComparison.Ordinal);
        Assert.Contains("20 mm", list, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(list, Regex.Escape(name) + " "));
        Assert.Contains("+0.2 -0.1", after, StringComparison.Ordinal);
        Assert.Contains("nothing missing", side, StringComparison.Ordinal);
        Assert.Contains("1 centre lines", side, StringComparison.Ordinal);
        Assert.Contains("nothing missing", front, StringComparison.Ordinal);
        Assert.True(picture.IsError != true);
    });

    [Fact]
    public async Task AToleranceSurvivesSavingTheModelAndReopening() => await InDrawing(async () =>
    {
        var file = Path.Combine(_root, "Datum", "toleranced.slddrw");
        Ok(await DrawingTools.ImportDimensions(_connection, _workspace));
        var name = Regex.Match(Ok(await DrawingTools.ListDimensions(_connection)), @"(D\d+@Sketch1) 80 mm").Groups[1].Value;
        Ok(await DrawingTools.Tolerance(_connection, _workspace, name, "symmetric", 0.05));

        var refused = Text(await DocumentTools.Save(_connection, _workspace, path: file));
        Assert.StartsWith("INVALID_ARGUMENT: block", refused, StringComparison.OrdinalIgnoreCase);
        var drawing = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Active).Id;
        var model = Ok(await DocumentTools.Open(_connection, _workspace, Block)).Split(' ')[1].TrimEnd('*');
        Ok(await DocumentTools.Save(_connection, _workspace, doc: model));
        Ok(await DocumentTools.Activate(_connection, _workspace, drawing));
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
        Ok(await DocumentTools.Open(_connection, _workspace, file));

        var reopened = Ok(await DrawingTools.ListDimensions(_connection));
        Console.WriteLine(reopened);
        Assert.Contains("±0.05", reopened, StringComparison.Ordinal);
        await DocumentTools.Close(_connection, doc: Block, discardChanges: true);
    });

    [Fact]
    public async Task TheCheckFindsTheWeekTenMistakesAndPassesOnceFixed() => await InDrawing(async () =>
    {
        var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).ToArray();

        var before = Ok(await DrawingTools.Check(_connection));
        Console.WriteLine(before);
        Assert.Contains("side-on hole", before, StringComparison.Ordinal);
        Assert.Contains("hidden lines are off", before, StringComparison.Ordinal);
        Assert.Contains("Not shown: D1@Boss-Extrude1 20 mm", before, StringComparison.Ordinal);

        Ok(await DrawingTools.ImportDimensions(_connection, _workspace));
        foreach (var view in views.Take(3))
        {
            Ok(await DrawingTools.Marks(_connection, _workspace, view));
            Ok(await DrawingTools.SetView(_connection, _workspace, view, display: "hidden"));
        }

        var after = Ok(await DrawingTools.Check(_connection));
        Console.WriteLine(after);
        Assert.DoesNotContain("side-on hole", after, StringComparison.Ordinal);
        Assert.DoesNotContain("hidden lines are off", after, StringComparison.Ordinal);
        Assert.Contains("4 of 4 model sizes shown", after, StringComparison.Ordinal);
    });

    [Fact]
    public async Task TheCheckSeesAViewOffTheSheetAndOverlappingViews() => await InDrawing(async () =>
    {
        var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).ToArray();

        Ok(await DrawingTools.SetView(_connection, _workspace, views[3], x: 300, y: 100));
        Ok(await DrawingTools.SetView(_connection, _workspace, views[2], x: 62, y: 86));
        var report = Ok(await DrawingTools.Check(_connection));
        Console.WriteLine(report);

        Assert.Contains("Drawing View4 runs off the sheet", report, StringComparison.Ordinal);
        Assert.Contains("overlaps", report, StringComparison.Ordinal);
    });

    [Fact]
    public async Task TheTitleBlockShowsWhatIsSet() => await InDrawing(async () =>
    {
        var reply = Ok(await DrawingTools.Title(_connection, _workspace,
        [
            new TitleField { Name = "DrawnBy", Value = "JM" },
            new TitleField { Name = "Revision", Value = "B" },
            new TitleField { Name = "Description", Value = "CLAMP BASE" },
        ]));
        var missing = Text(await DrawingTools.Title(_connection, _workspace, [new TitleField { Name = "Nonsense", Value = "x" }]));
        Ok(await DocumentTools.Export(_connection, _workspace, Path.Combine(CaptureFolder, "drawing-title.pdf"), overwrite: true));
        Console.WriteLine($"{reply}{missing}");

        Assert.DoesNotContain("NOT showing", reply, StringComparison.Ordinal);
        Assert.Contains("block.SLDPRT, which now needs saving", reply, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("DrawnBy", missing, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AnAssemblyDrawingHasAMatchingBomBalloonsAndExplodeLines()
    {
        await MakeBlock();
        var pin = Path.Combine(_root, "Datum", "pin.sldprt");
        var assembly = Path.Combine(_root, "Datum", "pair.sldasm");
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "circle", Cx = 0, Cy = 0, Radius = 6, Dimension = true }]));
        var sketch = FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value;
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 40));
        Ok(await DocumentTools.Save(_connection, _workspace, path: pin));
        Ok(await DocumentTools.Close(_connection));

        Ok(await DocumentTools.New(_connection, _workspace, "assembly"));
        Ok(await AssemblyTools.Add(_connection, _workspace, Block));
        var pinA = Ok(await AssemblyTools.Add(_connection, _workspace, pin, x: 20, z: 40)).Split(' ')[1];
        var pinB = Ok(await AssemblyTools.Add(_connection, _workspace, pin, x: -20, z: 40)).Split(' ')[1];
        Ok(await AssemblyTools.AddConfiguration(_connection, _workspace, "Exploded"));
        Ok(await AssemblyTools.Explode(_connection, _workspace, [new ExplodeStepSpec { Component = pinA, Axis = "z", Distance = 30 }, new ExplodeStepSpec { Component = pinB, Axis = "z", Distance = 60 }]));
        Ok(await AssemblyTools.ShowExplode(_connection, true));
        var lines = Ok(await DrawingTools.ExplodeLines(_connection, _workspace));
        Ok(await DocumentTools.Save(_connection, _workspace, path: assembly));
        Ok(await DocumentTools.Close(_connection));
        Console.WriteLine(lines);

        Ok(await DrawingTools.New(_connection, _workspace, sheet: "b", scale: "1:2"));
        try
        {
            var view = Handle(Ok(await DrawingTools.AddView(_connection, _workspace, assembly, "iso", 120, 230)));
            Ok(await DrawingTools.SetView(_connection, _workspace, view, configuration: "Exploded", exploded: true));
            var noBom = Ok(await DrawingTools.Check(_connection));
            var rows = Ok(await DrawingTools.AddBom(_connection, _workspace, view, 225, 260));
            var balloons = Ok(await DrawingTools.Balloons(_connection, _workspace, view));
            var final = Ok(await DrawingTools.ReadBom(_connection));
            var report = Ok(await DrawingTools.Check(_connection));
            await ViewTools.Capture(_connection, _workspace, size: 1400, path: Path.Combine(CaptureFolder, "drawing-assembly.png"), overwrite: true);
            Ok(await DocumentTools.Export(_connection, _workspace, Path.Combine(CaptureFolder, "drawing-assembly.pdf"), overwrite: true));
            Console.WriteLine($"{noBom}{rows}{balloons}{final}{report}");

            Assert.Equal(2, Regex.Count(lines, ", 60 to "));
            Assert.Contains(", 90 mm", lines, StringComparison.Ordinal);
            Assert.Contains(", 120 mm", lines, StringComparison.Ordinal);
            Assert.Contains("no BOM table", noBom, StringComparison.Ordinal);
            Assert.Contains("2 x pin", final, StringComparison.Ordinal);
            Assert.Contains("1 x block", final, StringComparison.Ordinal);
            Assert.Contains("points at pin-", balloons, StringComparison.Ordinal);
            Assert.DoesNotContain("BOM", report, StringComparison.Ordinal);
            Assert.DoesNotContain("Balloon", report, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task RequirementsCheckARealDrawingAndExportsLandOnDisk() => await InDrawing(async () =>
    {
        var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).ToArray();
        Ok(await DrawingTools.SetView(_connection, _workspace, views[0], x: 70, y: 120));
        Ok(await CheckTools.SetRequirements(_connection, _workspace,
        [
            new RequirementSpec { Id = "r1", Text = "Four views", Check = "view_count", Target = 4 },
            new RequirementSpec { Id = "r2", Text = "Shows the 80 width", Check = "shown_dimension", Target = 80 },
            new RequirementSpec { Id = "r3", Text = "Clean", Check = "drawing_clean" },
            new RequirementSpec { Id = "r4", Text = "Wrong place for mass", Check = "mass", Target = 5 },
        ]));

        var report = Ok(await CheckTools.CheckRequirements(_connection, _workspace));
        var pdf = Ok(await DocumentTools.Export(_connection, _workspace, Path.Combine(_root, "Datum", "out.pdf")));
        var dxf = Ok(await DocumentTools.Export(_connection, _workspace, Path.Combine(_root, "Datum", "out.dxf")));
        Console.WriteLine($"{report}{pdf}{dxf}");

        Assert.Contains("PASS r1", report, StringComparison.Ordinal);
        Assert.Contains("FAIL r2", report, StringComparison.Ordinal);
        Assert.Contains("FAIL r3", report, StringComparison.Ordinal);
        Assert.Contains("UNVERIFIED r4", report, StringComparison.Ordinal);
        Assert.True(new FileInfo(Path.Combine(_root, "Datum", "out.pdf")).Length > 1000);
        Assert.True(new FileInfo(Path.Combine(_root, "Datum", "out.dxf")).Length > 1000);
    });

    [Fact]
    public async Task HoleMarksLandOnceInEveryView()
    {
        var plate = Path.Combine(_root, "Datum", "plate.sldprt");
        await MakePlate(plate);
        Ok(await DrawingTools.New(_connection, _workspace, model: plate, scale: "1:2"));
        try
        {
            var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).ToArray();
            foreach (var view in views.Take(3))
            {
                Ok(await DrawingTools.Marks(_connection, _workspace, view));
            }

            var top = Ok(await DrawingTools.Marks(_connection, _workspace, views[1]));
            var right = Ok(await DrawingTools.Marks(_connection, _workspace, views[2]));
            var uncut = Ok(await DrawingTools.Section(_connection, _workspace, views[0], "vertical", 0, 150, 60));
            var report = Ok(await DrawingTools.Check(_connection));
            Console.WriteLine($"{top}{right}{uncut}{report}");

            Assert.Contains("2 centre marks, 0 centre lines, nothing missing", top, StringComparison.Ordinal);
            Assert.Contains("0 centre marks, 1 centre lines, nothing missing", right, StringComparison.Ordinal);
            Assert.DoesNotContain("no centre", report, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task MarksOnAPartFarFromItsOriginLandOnce()
    {
        var far = Path.Combine(_root, "Datum", "far.sldprt");
        Directory.CreateDirectory(Path.GetDirectoryName(far)!);
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 400, Cy = 250, Width = 80, Height = 50 }, new SketchEntitySpec { Type = "circle", Cx = 420, Cy = 250, Radius = 6 }]));
        Ok(await FeatureTools.Extrude(_connection, _workspace, FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value, depth: 20));
        Ok(await DocumentTools.Save(_connection, _workspace, path: far));
        Ok(await DocumentTools.Close(_connection));
        Ok(await DrawingTools.New(_connection, _workspace, scale: "1:2"));
        try
        {
            var view = Handle(Ok(await DrawingTools.AddView(_connection, _workspace, far, "front", 120, 130)));
            Ok(await DrawingTools.Marks(_connection, _workspace, view));
            var again = Ok(await DrawingTools.Marks(_connection, _workspace, view));
            var report = Ok(await DrawingTools.Check(_connection));
            Console.WriteLine($"{again}{report}");

            Assert.Contains("1 centre marks", again, StringComparison.Ordinal);
            Assert.DoesNotContain("no centre mark", report, StringComparison.Ordinal);
            Assert.Contains("1:2 at", Ok(await DrawingTools.ListViews(_connection)), StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task ASectionThroughAHoleWantsItsCentreLine() => await InDrawing(async () =>
    {
        var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).ToArray();
        var section = Handle(Ok(await DrawingTools.Section(_connection, _workspace, views[0], "vertical", 20, 170, 60)));
        var before = Ok(await DrawingTools.Check(_connection));
        var marks = Ok(await DrawingTools.Marks(_connection, _workspace, section));
        Console.WriteLine($"{before}{marks}");

        Assert.Contains("Section View A-A has 1 side-on hole with no centre line", before, StringComparison.Ordinal);
        Assert.Contains("1 centre lines, nothing missing", marks, StringComparison.Ordinal);
    });

    [Fact]
    public async Task ANewViewReportsItsRealSizeAndAHoleSeenEdgeOnIsALine()
    {
        await MakeBlock();
        Ok(await DrawingTools.New(_connection, _workspace, scale: "1:2"));
        try
        {
            var front = Ok(await DrawingTools.AddView(_connection, _workspace, Block, "front", 100, 120));
            var top = Ok(await DrawingTools.AddView(_connection, _workspace, Block, "top", 100, 170));
            var inTop = Ok(await DrawingTools.Entities(_connection, Handle(top), kind: "circle"));
            var inFront = Ok(await DrawingTools.Entities(_connection, Handle(front), kind: "circle"));
            Console.WriteLine($"{front}{top}{inFront}{inTop}");

            Assert.Contains("40 x 25 mm", front, StringComparison.Ordinal);
            Assert.Contains("40 x 10 mm", top, StringComparison.Ordinal);
            Assert.Contains("circle centre", inFront, StringComparison.Ordinal);
            Assert.Equal("No visible edges\n", inTop);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task AnOffOriginPartLaysOutAndDimensionsItself()
    {
        var far = Path.Combine(_root, "Datum", "far.sldprt");
        Directory.CreateDirectory(Path.GetDirectoryName(far)!);
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 400, Cy = 250, Width = 120, Height = 60 }, new SketchEntitySpec { Type = "circle", Cx = 420, Cy = 250, Radius = 8 }]));
        Ok(await FeatureTools.Extrude(_connection, _workspace, FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value, depth: 30));
        Ok(await DocumentTools.Save(_connection, _workspace, path: far));
        Ok(await DocumentTools.Close(_connection));
        var file = Path.Combine(_root, "Datum", "far.slddrw");
        Ok(await DrawingTools.New(_connection, _workspace, model: far));
        try
        {
            var layout = Ok(await DrawingTools.Layout(_connection, _workspace));
            var added = Ok(await DrawingTools.AutoDimensions(_connection, _workspace));
            var tidied = Ok(await DrawingTools.TidyDimensions(_connection, _workspace));
            foreach (var view in Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).Take(3))
            {
                Ok(await DrawingTools.Marks(_connection, _workspace, view));
            }

            var report = Ok(await DrawingTools.Check(_connection));
            Ok(await DocumentTools.Save(_connection, _workspace, path: file));
            Ok(await DocumentTools.Close(_connection));
            Ok(await DocumentTools.Open(_connection, _workspace, file));
            var reopened = Ok(await DrawingTools.ListViews(_connection));
            Console.WriteLine($"{layout}{added}{tidied}{report}{reopened}");

            Assert.DoesNotContain("WARNING", layout, StringComparison.Ordinal);
            Assert.Contains(" 120 mm", added, StringComparison.Ordinal);
            Assert.Contains(" 60 mm", added, StringComparison.Ordinal);
            Assert.Contains(" 16 mm", added, StringComparison.Ordinal);
            Assert.Contains(" 80 mm", added, StringComparison.Ordinal);
            Assert.Contains(" 30 mm", added, StringComparison.Ordinal);
            Assert.Contains("No problems found", report, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task ANoteCanBeEditedInPlace() => await InDrawing(async () =>
    {
        Ok(await DrawingTools.Note(_connection, _workspace, "FIRST", 60, 190));
        var name = Ok(await DrawingTools.ListAnnotations(_connection)).Split('\n').Single(l => l.Contains("FIRST", StringComparison.Ordinal)).Split(' ')[0];

        Assert.Equal("Note: SECOND\n", Ok(await DrawingTools.EditNote(_connection, _workspace, name, "SECOND")));
    });

    [Fact]
    public async Task ViewEntitiesGiveTheRealEnds() => await InDrawing(async () =>
    {
        var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).ToArray();
        var at = ViewPosition().Match(views[0]);
        var (x, y) = (double.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture), double.Parse(at.Groups[2].Value, CultureInfo.InvariantCulture));
        var entities = Ok(await DrawingTools.Entities(_connection, Handle(views[0])));
        Console.WriteLine(entities);

        // The 80 x 50 block at 1:2, centred on the view, so every corner is 20 and 12.5 mm from it.
        foreach (Match line in EntityLine().Matches(entities))
        {
            var ends = line.Groups.Values.Skip(1).Select(g => double.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
            Assert.Equal(20, Math.Abs(ends[0] - x), 1);
            Assert.Equal(12.5, Math.Abs(ends[1] - y), 1);
            Assert.Equal(20, Math.Abs(ends[2] - x), 1);
            Assert.Equal(12.5, Math.Abs(ends[3] - y), 1);
        }

        Assert.Matches("circle centre", entities);
    });

    [Fact]
    public async Task TextOnTheTitleBlockIsCaughtAndCanBeMovedOrDeleted() => await InDrawing(async () =>
    {
        var file = Path.Combine(_root, "Datum", "text.slddrw");
        var view = Handle(Ok(await DrawingTools.ListViews(_connection)));
        Ok(await DrawingTools.Note(_connection, _workspace, "CHECK THIS NOTE", 200, 30));
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        var saved = Ok(await DocumentTools.List(_connection, _workspace, CancellationToken.None));
        var caught = Ok(await DrawingTools.Check(_connection));
        var after = Ok(await DocumentTools.List(_connection, _workspace, CancellationToken.None));
        var list = Ok(await DrawingTools.ListAnnotations(_connection));
        Console.WriteLine($"{saved}{after}{caught}{list}");

        Assert.Equal(saved, after);
        Assert.Contains("'CHECK THIS NOTE' sits on the title block", caught, StringComparison.Ordinal);
        Assert.DoesNotContain("PROPRIETARY", list, StringComparison.Ordinal);

        var note = list.Split('\n').Single(l => l.Contains("CHECK THIS NOTE", StringComparison.Ordinal)).Split(' ')[0];
        Assert.Contains("at 60,190", Ok(await DrawingTools.MoveAnnotation(_connection, _workspace, note, 60, 190)), StringComparison.Ordinal);
        Assert.DoesNotContain("CHECK THIS NOTE", Ok(await DrawingTools.Check(_connection)), StringComparison.Ordinal);

        Ok(await DrawingTools.DeleteAnnotation(_connection, _workspace, note));
        Assert.DoesNotContain("CHECK THIS NOTE", Ok(await DrawingTools.ListAnnotations(_connection)), StringComparison.Ordinal);

        Ok(await DrawingTools.ImportDimensions(_connection, _workspace, view));
        var dimension = Ok(await DrawingTools.ListDimensions(_connection, view)).Split('\n')[0].Split(' ')[1];
        Ok(await DrawingTools.DeleteAnnotation(_connection, _workspace, dimension));
        Assert.DoesNotContain(dimension, Ok(await DrawingTools.ListDimensions(_connection, view)), StringComparison.Ordinal);
    });

    [Fact]
    public async Task TheTitleBlockStatesTheDrawingsUnits() => await InDrawing(async () =>
    {
        var file = Path.Combine(_root, "Datum", "units.slddrw");
        var metric = Ok(await DrawingTools.Check(_connection));
        Ok(await DocumentTools.Export(_connection, _workspace, Path.Combine(CaptureFolder, "drawing-units.pdf"), overwrite: true));
        Ok(await DrawingTools.SetSheet(_connection, _workspace, units: "inches"));
        var imperial = Ok(await DrawingTools.Check(_connection));
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));
        Ok(await DocumentTools.Close(_connection));
        Ok(await DocumentTools.Open(_connection, _workspace, file));
        var reopened = Ok(await DrawingTools.Check(_connection));
        Console.WriteLine($"{metric}{imperial}{reopened}");

        Assert.DoesNotContain("title block says", metric, StringComparison.Ordinal);
        Assert.DoesNotContain("title block says", imperial, StringComparison.Ordinal);
        Assert.DoesNotContain("title block says", reopened, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AViewsSizeIsItsGeometry() => await InDrawing(async () =>
    {
        var view = Ok(await DrawingTools.ListViews(_connection)).Split('\n')[0];

        Assert.Contains("40 x 25 mm", view, StringComparison.Ordinal);
    });

    [Fact]
    public async Task ADetailViewRefusesMarksAndAFileNameFindsADrawing() => await InDrawing(async () =>
    {
        var view = Handle(Ok(await DrawingTools.ListViews(_connection)));
        var detail = Handle(Ok(await DrawingTools.Detail(_connection, _workspace, view, 20, 0, 10, "2:1", 230, 60)));
        var file = Path.Combine(_root, "Datum", "named.slddrw");
        Ok(await DocumentTools.Save(_connection, _workspace, path: file));

        Assert.StartsWith("INVALID_ARGUMENT: ", Text(await DrawingTools.Marks(_connection, _workspace, detail)), StringComparison.Ordinal);
        Ok(await DrawingTools.ListViews(_connection, "named.SLDDRW"));
    });

    [Fact]
    public async Task ViewsCanBeDeletedAndTheSheetRescaled() => await InDrawing(async () =>
    {
        var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).ToArray();
        var rescaled = Ok(await DrawingTools.SetSheet(_connection, _workspace, "1:4"));
        var deleted = Ok(await DrawingTools.DeleteView(_connection, _workspace, views[3]));
        var left = Ok(await DrawingTools.ListViews(_connection));
        Console.WriteLine($"{rescaled}{deleted}{left}");

        Assert.StartsWith("Sheet scale 1:4\n", rescaled, StringComparison.Ordinal);
        Assert.Contains("1:4 at", rescaled, StringComparison.Ordinal);
        Assert.Equal(3, left.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
    });

    [Fact]
    public async Task ASingleViewKeepsTheSheetScale()
    {
        await MakeBlock();
        Ok(await DrawingTools.New(_connection, _workspace, scale: "1:1"));
        try
        {
            var view = Ok(await DrawingTools.AddView(_connection, _workspace, Block, "front", 100, 120));
            Console.WriteLine(view);

            Assert.Contains("1:1 at", view, StringComparison.Ordinal);
            Assert.DoesNotContain("scale", Ok(await DrawingTools.Check(_connection)), StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task MovingAViewSaysWhatMovedWithIt() => await InDrawing(async () =>
    {
        var views = Ok(await DrawingTools.ListViews(_connection)).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Handle).ToArray();
        var reply = Ok(await DrawingTools.SetView(_connection, _workspace, views[0], y: 190));
        Console.WriteLine(reply);

        Assert.Contains("moved with it", reply, StringComparison.Ordinal);
        Assert.Contains("now runs off the sheet", reply, StringComparison.Ordinal);
    });

    [Fact]
    public async Task ClosingADrawingLeavesTheSameNamedModelOpen()
    {
        await MakeBlock();
        Ok(await DrawingTools.New(_connection, _workspace, model: Block));
        Ok(await DocumentTools.Save(_connection, _workspace, path: Path.Combine(_root, "Datum", "block.slddrw")));
        Ok(await DocumentTools.Open(_connection, _workspace, Block));
        var drawing = (await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)).Single(d => d.Type == DocumentType.Drawing);

        Ok(await DocumentTools.Close(_connection, drawing.Id));
        var left = await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None);
        Console.WriteLine(string.Join("\n", left.Select(d => d.Title)));

        Assert.Equal(DocumentType.Part, Assert.Single(left).Type);
        Ok(await DocumentTools.Close(_connection, discardChanges: true));
    }

    [Fact]
    public async Task ClosingADrawingWontLoseAHiddenModelsChanges() => await InDrawing(async () =>
    {
        var dimension = Ok(await DrawingTools.ImportDimensions(_connection, _workspace));
        var name = Ok(await DrawingTools.ListDimensions(_connection)).Split('\n')[0].Split(' ')[1];
        Ok(await DrawingTools.Tolerance(_connection, _workspace, name, "symmetric", 0.1));
        Console.WriteLine(dimension);

        Assert.StartsWith("UNSAVED_CHANGES: ", Text(await DocumentTools.Close(_connection)), StringComparison.Ordinal);
    });

    private async Task MakePlate(string path)
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Top"));
        Ok(await SketchTools.Add(_connection, _workspace,
        [
            new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 80, Height = 50, Dimension = true },
            new SketchEntitySpec { Type = "circle", Cx = -25, Cy = 0, Radius = 5, Dimension = true },
            new SketchEntitySpec { Type = "circle", Cx = 25, Cy = 0, Radius = 5, Dimension = true },
        ]));
        var sketch = FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value;
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 10));
        Ok(await DocumentTools.Save(_connection, _workspace, path: path));
        Ok(await DocumentTools.Close(_connection));
    }

    private async Task MakeBlock()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 80, Height = 50, Dimension = true }, new SketchEntitySpec { Type = "circle", Cx = 20, Cy = 0, Radius = 6, Dimension = true }]));
        var sketch = FeatHandle().Match(Ok(await SketchTools.End(_connection, _workspace))).Value;
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 20));
        Ok(await DocumentTools.Save(_connection, _workspace, path: Block));
        Ok(await DocumentTools.Close(_connection));
    }

    private async Task InDrawing(Func<Task> test)
    {
        await MakeBlock();
        Console.WriteLine(Ok(await DrawingTools.New(_connection, _workspace, model: Block, scale: "1:2")));
        try
        {
            await test();
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [GeneratedRegex(@"at ([-\d.]+),([-\d.]+) mm")]
    private static partial Regex ViewPosition();

    [GeneratedRegex(@"line ([-\d.]+),([-\d.]+) to ([-\d.]+),([-\d.]+)")]
    private static partial Regex EntityLine();

    private static string Handle(string reply) => reply.Split(' ')[0];

    private static string Text(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(result.Content[0]).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatHandle();
}
