using Datum.Core;

namespace Datum.Tests;

public sealed class DrawingAuditTests
{
    private static readonly SheetBox Clear = new(20, 100, 80, 140);

    [Fact]
    public void ACleanDrawingHasNoProblems()
    {
        Assert.Empty(DrawingAudit.Find(Facts([View()])));
    }

    [Fact]
    public void AnEmptyDrawingIsAProblem()
    {
        Assert.Equal(["The drawing has no views"], DrawingAudit.Find(Facts([])));
    }

    [Fact]
    public void HiddenLinesOffOnAViewWithAHoleSideOnIsFlagged()
    {
        var problem = Assert.Single(DrawingAudit.Find(Facts([View(side: 1, display: "removed")])));

        Assert.StartsWith("Front hides 1 hole axis from view", problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("hidden", "named", false)]
    [InlineData("removed", "section", false)]
    [InlineData("removed", "detail", false)]
    [InlineData("removed", "named", true)]
    public void HiddenLinesAreNotRequiredWhereTheyDoNotApply(string display, string kind, bool isometric)
    {
        Assert.Empty(DrawingAudit.Find(Facts([View(side: 1, display: display, kind: kind, isometric: isometric)])));
    }

    [Fact]
    public void ACentreMarkDoesNotExcuseAMissingCentreLine()
    {
        Assert.Equal(["Front has 1 side-on hole with no centre line. Use marks_add"], DrawingAudit.Find(Facts([View(missingLines: 1)])));
        Assert.Equal(["Front has 2 hole circles with no centre mark. Use marks_add"], DrawingAudit.Find(Facts([View(missingMarks: 2)])));
    }

    [Fact]
    public void AModelDimensionNotShownIsListedByNameAndValue()
    {
        var facts = Facts([View()]) with { ModelDimensions = [new ModelDimension("D1@V-groove", 0.5, " mm")] };

        Assert.Equal(["Not shown: D1@V-groove 0.5 mm"], DrawingAudit.Find(facts));
    }

    [Fact]
    public void AReferenceDimensionWithTheSameValueCovers()
    {
        var facts = Facts([View()]) with
        {
            ModelDimensions = [new ModelDimension("D1@V-groove", 0.5, " mm")],
            Shown = [new SheetDimension("view#1", "RD1@Drawing View1", 0.5, " mm", false, null)],
        };

        Assert.Empty(DrawingAudit.Find(facts));
    }

    [Fact]
    public void ADimensionByNameCoversEvenIfItsValueChanged()
    {
        var facts = Facts([View()]) with
        {
            ModelDimensions = [new ModelDimension("D1@Boss", 20, " mm")],
            Shown = [new SheetDimension("view#1", "D1@Boss", 20, " mm", false, null)],
        };

        Assert.Empty(DrawingAudit.Find(facts));
    }

    [Fact]
    public void DanglingAnnotationsAreFlagged()
    {
        Assert.Equal(["Front: 'D1@Gone' is attached to nothing"], DrawingAudit.Find(Facts([View(annotations: [new SheetAnnotation("D1@Gone", 50, 120, true)])])));
    }

    [Fact]
    public void LayoutProblemsAreFlagged()
    {
        var off = View(outline: new SheetBox(260, 100, 300, 140));
        var first = View();
        var second = View(name: "Top", outline: new SheetBox(70, 130, 120, 170));

        Assert.Contains("Front runs off the sheet", DrawingAudit.Find(Facts([off])));
        Assert.Contains("Front overlaps Top", DrawingAudit.Find(Facts([first, second])));
    }

    [Fact]
    public void TheTitleBlockIsKeptClear()
    {
        var facts = Facts([View(outline: new SheetBox(100, 10, 200, 60), annotations: [new SheetAnnotation("80", 150, 20, false, new SheetBox(145, 18, 155, 22))])]) with
        {
            TitleBlock = [new SheetBox(90, 20, 270, 20.1)],
            SheetNotes = [new SheetAnnotation("ALL EDGES BROKEN", 200, 20, false, new SheetBox(195, 18, 240, 22))],
        };

        var problems = DrawingAudit.Find(facts);

        Assert.Contains("Front overlaps the title block", problems);
        Assert.Contains("Front: '80' sits on the title block", problems);
        Assert.Contains("Note: 'ALL EDGES BROKEN' sits on the title block", problems);
    }

    [Fact]
    public void AViewClearOfTheTitleBlockPasses()
    {
        var facts = Facts([View(outline: new SheetBox(30, 70, 60, 90))]) with { TitleBlock = [new SheetBox(90, 20, 270, 20.1), new SheetBox(90, 60, 270, 60.1)] };

        Assert.Empty(DrawingAudit.Find(facts));
    }

    [Fact]
    public void TextPastTheBorderIsFlaggedThoughItIsOnTheSheet()
    {
        var facts = Facts([View(annotations: [new SheetAnnotation("80", 268, 150, false, new SheetBox(264, 148, 272, 152))])]) with { Frame = new SheetBox(12.7, 12.7, 266.7, 203.2) };

        Assert.Equal(["Front: '80' runs past the border"], DrawingAudit.Find(facts));
    }

    [Fact]
    public void ABomOverTheBorderIsFlagged()
    {
        var facts = Facts([View()]) with { Frame = new SheetBox(12.7, 12.7, 266.7, 203.2), BomBox = new SheetBox(150, 180, 250, 210) };

        Assert.Equal(["The BOM runs off the sheet"], DrawingAudit.Find(facts));
    }

    [Fact]
    public void OverlappingTextIsFlaggedAcrossViews()
    {
        var first = View(annotations: [new SheetAnnotation("D1", 150, 150, false, new SheetBox(145, 148, 155, 152))]);
        var second = View(name: "Top", outline: new SheetBox(100, 100, 130, 130), annotations: [new SheetAnnotation("D2", 152, 150, false, new SheetBox(150, 149, 160, 153))]);

        Assert.Equal(["'D1' overlaps 'D2'"], DrawingAudit.Find(Facts([first, second])));
    }

    [Fact]
    public void TextOnAnotherViewIsFlaggedButNotOnItsOwn()
    {
        var first = View(annotations: [new SheetAnnotation("D1", 50, 120, false, new SheetBox(45, 118, 55, 122))]);
        var second = View(name: "Top", outline: new SheetBox(100, 100, 130, 130), annotations: [new SheetAnnotation("D2", 50, 130, false, new SheetBox(45, 128, 55, 132))]);

        Assert.Equal(["Top: 'D2' sits on Front"], DrawingAudit.Find(Facts([first, second])));
    }

    [Fact]
    public void TextOnTheBomIsFlagged()
    {
        var facts = Facts([View()]) with
        {
            BomBox = new SheetBox(150, 150, 250, 200),
            SheetNotes = [new SheetAnnotation("NOTE", 160, 160, false, new SheetBox(155, 158, 170, 162))],
        };

        Assert.Equal(["Note: 'NOTE' sits on the BOM"], DrawingAudit.Find(facts));
    }

    [Fact]
    public void ADiameterCoversASketchedRadiusAndSettingsNeedNoDimension()
    {
        var facts = Facts([View()]) with
        {
            ModelDimensions = [new ModelDimension("D7@Sketch1", 48, " mm"), new ModelDimension("D1@Revolve1", 360, "°"), new ModelDimension("D1@CirPattern1", 72000, " mm")],
            Shown = [new SheetDimension("view#1", "RD1@Drawing View1", 96, " mm", false, null)],
        };

        Assert.Empty(DrawingAudit.MissingDimensions(facts));
    }

    [Fact]
    public void APositionFromTheOriginIsCountedAsNotChecked()
    {
        var facts = Facts([View()]) with { ModelDimensions = [new ModelDimension("D3@Sketch1", 340, " mm", Position: true)] };

        Assert.Empty(DrawingAudit.Find(facts));
        Assert.Contains("NOT CHECKED: 1 positions measured from the origin or a plane", DrawingAudit.Report("Sheet1", facts), StringComparison.Ordinal);
    }

    [Fact]
    public void TheBomIsMeasuredAgainstAViewsGeometry()
    {
        var view = View() with { Geometry = new SheetBox(20, 100, 40, 140) };

        Assert.Empty(DrawingAudit.Find(Facts([view]) with { BomBox = new SheetBox(50, 110, 100, 130) }));
    }

    [Fact]
    public void TextWithAnotherDimensionsLineThroughItIsAProblem()
    {
        var crossed = new SheetAnnotation("RD1", 50, 50, false, new SheetBox(45, 48, 55, 52), "RD1");
        var crossing = new SheetAnnotation("RD2", 90, 90, false, new SheetBox(85, 88, 95, 92), "RD2") { Lines = [new SheetSegment(40, 50, 60, 50)] };
        var facts = Facts([View() with { Annotations = [crossed, crossing] }]);

        Assert.Contains("'RD1' has a line of 'RD2' through it", DrawingAudit.Find(facts));
    }

    [Fact]
    public void SkippedChecksAreLeftOutAndSaidToBe()
    {
        var facts = Facts([View(missingMarks: 2)]) with { ModelDimensions = [new ModelDimension("D1@Sweep1", 25.4, " mm")] };

        Assert.Equal(2, DrawingAudit.Find(facts).Count);
        Assert.Empty(DrawingAudit.Find(facts, DrawingAudit.Skippable));
        Assert.Contains("NOT CHECKED, as asked: dimensions, marks, bom\n", DrawingAudit.Report("Sheet1", facts, DrawingAudit.Skippable), StringComparison.Ordinal);
    }

    [Fact]
    public void ATitleBlockInTheWrongUnitsIsFlagged()
    {
        var facts = Facts([View()]) with { FormatUnits = "inches" };

        Assert.Equal(["The title block says dimensions are in inches, but they show mm. Use sheet_set with units"], DrawingAudit.Find(facts));
        Assert.Empty(DrawingAudit.Find(facts with { FormatUnits = "mm" }));
        Assert.Empty(DrawingAudit.Find(facts with { FormatUnits = null }));
    }

    [Fact]
    public void ADetailViewNeedsNoMarksOfItsOwn()
    {
        Assert.Empty(DrawingAudit.Find(Facts([View(kind: "detail", missingMarks: 2, missingLines: 1)])));
    }

    [Fact]
    public void ATitleBlockScaleThatDisagreesWithTheMainViewIsFlagged()
    {
        var facts = Facts([View()]) with { SheetScale = "1:2", TitleBlock = [new SheetBox(90, 20, 270, 20.1)] };

        Assert.Equal(["The title block says 1:2, but Front is 1:1. Use sheet_set"], DrawingAudit.Find(facts));
        Assert.Empty(DrawingAudit.Find(facts with { SheetScale = "1:1" }));
    }

    [Fact]
    public void TwoFeaturesOfTheSameSizeNeedTwoDimensions()
    {
        var facts = Facts([View()]) with
        {
            ModelDimensions = [new ModelDimension("D1@Boss", 25, " mm"), new ModelDimension("D4@Sketch1", 25, " mm")],
            Shown = [new SheetDimension("view#1", "D1@Boss", 25, " mm", false, null)],
        };

        Assert.Equal(["Not shown: D4@Sketch1 25 mm"], DrawingAudit.Find(facts));
    }

    [Fact]
    public void OneDimensionCoversASizeAndTheReportSaysSo()
    {
        var facts = Facts([View()]) with
        {
            ModelDimensions = [new ModelDimension("D1@Boss", 25, " mm"), new ModelDimension("D4@Sketch1", 25, " mm")],
            Shown = [new SheetDimension("view#1", "RD1@Drawing View1", 25, " mm", false, null)],
        };

        Assert.Empty(DrawingAudit.MissingDimensions(facts));
        Assert.Contains("NOT CHECKED: 1 sizes share a value", DrawingAudit.Report("Sheet1", facts), StringComparison.Ordinal);
    }

    [Fact]
    public void AStaleOrBrokenDrawingIsFlagged()
    {
        var facts = Facts([View()]) with { NeedsRebuild = true, MissingModels = [@"C:\gone\part.sldprt"] };

        Assert.Equal(["The drawing needs a rebuild", @"Missing model: C:\gone\part.sldprt"], DrawingAudit.Find(facts));
    }

    [Fact]
    public void AnAssemblyDrawingNeedsABomThatMatches()
    {
        BomLine[] actual = [new("pin.SLDPRT", "Default", 2, null, null), new("base.SLDPRT", "Default", 1, null, null)];

        Assert.Equal(["An assembly drawing with no BOM table"], DrawingAudit.Find(Facts([View()]) with { ActualBom = actual }));

        var wrong = DrawingAudit.Find(Facts([View()]) with { ActualBom = actual, BomRows = [new BomRow("1", "pin", "", 3)] });

        Assert.Equal(["The BOM shows 3 of pin, but the assembly has 2", "The BOM has no row for base"], wrong);
    }

    [Fact]
    public void ABomOffTheSheetOrOverAViewIsFlagged()
    {
        Assert.Equal(["The BOM runs off the sheet"], DrawingAudit.Find(Facts([View()]) with { BomBox = new SheetBox(150, 150, 320, 200) }));
        Assert.Equal(["The BOM overlaps Front"], DrawingAudit.Find(Facts([View()]) with { BomBox = new SheetBox(50, 110, 100, 130) }));
    }

    [Fact]
    public void BalloonsMustMatchTheBom()
    {
        BomRow[] rows = [new("1", "base", "", 1), new("2", "pin", "", 2)];
        var facts = Facts([View()]) with
        {
            BomRows = rows,
            Balloons = [new BalloonInfo("1", "pin-1", false), new BalloonInfo("7", "bar-1", false), new BalloonInfo("8", null, true)],
        };

        Assert.Equal(
            [
                "Balloon 8 points at nothing",
                "BOM item 2 (pin) has no balloon",
                "Balloon 1 points at pin-1, but item 1 is base",
                "Balloon 7 matches no BOM item",
            ],
            DrawingAudit.Find(facts));
    }

    [Fact]
    public void GoodBalloonsPass()
    {
        var facts = Facts([View()]) with { BomRows = [new BomRow("1", "pin", "", 2)], Balloons = [new BalloonInfo("1", "pin-1", false)] };

        Assert.Empty(DrawingAudit.Find(facts));
    }

    [Fact]
    public void TheReportCountsDimensionsAndSaysWhatItDoesNotProve()
    {
        var facts = Facts([View()]) with { ModelDimensions = [new ModelDimension("D1@A", 1, " mm"), new ModelDimension("D2@A", 2, " mm")], Shown = [new SheetDimension("view#1", "D1@A", 1, " mm", false, null)] };

        Assert.Equal(
            "Sheet1: 1 views, 1 of 2 model sizes shown\nProblems (1):\n- Not shown: D2@A 2 mm\nThese checks passing doesn't prove the drawing meets its requirements, and they don't see every overlap in a tight gap. Look at the sheet.\n",
            DrawingAudit.Report("Sheet1", facts));
    }

    private static DrawingFacts Facts(IReadOnlyList<ViewFacts> views) => new(279.4, 215.9, views, [], [], [], false);

    private static ViewFacts View(
        string name = "Front",
        int side = 0,
        string display = "hidden",
        string kind = "named",
        bool isometric = false,
        int missingMarks = 0,
        int missingLines = 0,
        SheetBox? outline = null,
        IReadOnlyList<SheetAnnotation>? annotations = null) =>
        new(new ViewInfo("view#1", name, "Sheet1", kind, "part.SLDPRT", "1:1", 50, 120, 60, 40, display, 0, 0, 0), outline ?? Clear, isometric, side, missingMarks, missingLines, annotations ?? []);
}
