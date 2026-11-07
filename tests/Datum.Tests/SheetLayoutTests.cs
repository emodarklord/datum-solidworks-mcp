using Datum.Core;

namespace Datum.Tests;

public sealed class SheetLayoutTests
{
    private static readonly SheetBox Sheet = new(12.7, 60, 266.7, 203.2);

    private static LaidView[] Standard(double width, double height, double depth) =>
    [
        new("view#1", ViewRole.Main, width, height),
        new("view#2", ViewRole.Above, width, depth),
        new("view#3", ViewRole.Beside, depth, height),
        new("view#4", ViewRole.Pictorial, width * 0.8, height),
    ];

    [Theory]
    [InlineData(80, 50, 20, "1:2")]
    [InlineData(800, 500, 200, "1:12")]
    [InlineData(1220, 25, 25, "1:8")]
    [InlineData(10, 10, 10, "2:1")]
    public void TheLargestStandardScaleThatFitsIsChosen(double width, double height, double depth, string expected)
    {
        Assert.Equal(expected, SheetLayout.ChooseScale(Standard(width, height, depth), Sheet));
    }

    [Fact]
    public void ViewsLandOnTheSheetClearOfEachOther()
    {
        var views = Standard(70, 40, 20);

        var places = SheetLayout.Place(views, Sheet);
        var boxes = places.Select(p =>
        {
            var view = views.Single(v => v.Handle == p.Handle);
            return new SheetBox(p.X - (view.Width / 2), p.Y - (view.Height / 2), p.X + (view.Width / 2), p.Y + (view.Height / 2));
        }).ToList();

        Assert.Equal(4, places.Count);
        Assert.All(boxes, box => Assert.True(box.Within(Sheet)));
        for (var i = 0; i < boxes.Count; i++)
        {
            for (var j = i + 1; j < boxes.Count; j++)
            {
                Assert.False(boxes[i].Overlaps(boxes[j]));
            }
        }
    }

    [Fact]
    public void AViewAboveTheMainViewLinesUpWithIt()
    {
        var places = SheetLayout.Place(Standard(70, 40, 20), Sheet);

        Assert.Equal(places.Single(p => p.Handle == "view#1").X, places.Single(p => p.Handle == "view#2").X);
    }

    [Fact]
    public void TheUsableAreaSitsAboveTheTitleBlock()
    {
        var facts = new DrawingFacts(279.4, 215.9, [], [], [], [], false) { Frame = new SheetBox(12.7, 12.7, 266.7, 203.2), TitleBlock = [new SheetBox(100, 20, 260, 62)] };

        Assert.Equal(68, SheetLayout.Usable(facts).Bottom);
    }

    [Theory]
    [InlineData(100, 170, ViewRole.Above)]
    [InlineData(200, 100, ViewRole.Beside)]
    public void AProjectedViewTakesItsRoleFromWhereItSits(double x, double y, ViewRole expected)
    {
        var main = new ViewFacts(View(), new SheetBox(60, 80, 140, 120), false, 0, 0, 0, []) { Orientation = "front" };
        var projected = new ViewFacts(View() with { Handle = "view#2", Kind = "projected", X = x, Y = y }, new SheetBox(0, 0, 10, 10), false, 0, 0, 0, []);

        Assert.Equal(expected, SheetLayout.RoleOf(projected, [main, projected]));
    }

    private static ViewInfo View() => new("view#1", "Drawing View1", "Sheet1", "named", "p.SLDPRT", "1:1", 100, 100, 80, 40, "hidden", 0, 0, 0);

    [Fact]
    public void AnEndOnCircleGetsADiameterAndAnOutlineGetsItsSizes()
    {
        ViewEntity[] entities =
        [
            new("edge#1", "line", 60, 80, 140, 80),
            new("edge#2", "line", 60, 120, 60, 80),
            new("edge#3", "line", 140, 80, 140, 120),
            new("edge#4", "circle", 100, 100, 110, 100),
        ];

        var picks = DimensionPlan.Choose(View(), entities, [], []);

        Assert.Equal(["edge#1", "edge#2", "edge#4", "edge#2", "edge#1"], picks.Select(p => p.First));
    }

    [Fact]
    public void AThinEdgeTheModelNeedsAndASlopedTubeGetSizes()
    {
        ViewEntity[] entities =
        [
            new("edge#1", "line", 60, 80, 140, 80),
            new("edge#2", "line", 60, 120, 60, 80),
            new("edge#3", "line", 60, 80, 60, 82),
            new("edge#4", "line", 70, 90, 100, 130),
            new("edge#5", "line", 80, 100, 80, 107),
        ];

        Assert.Equal(["edge#1", "edge#2", "edge#4", "edge#3"], DimensionPlan.Choose(View(), entities, [], [2]).Select(p => p.First));
    }

    [Fact]
    public void OneSlopedEdgeIsSizedJustOutsideItsOwnSide()
    {
        ViewEntity[] entities = [new("edge#1", "line", 110, 100, 130, 120), new("edge#2", "line", 112, 100, 131, 119)];

        var pick = Assert.Single(DimensionPlan.Choose(View(), entities, [], []));

        Assert.True(pick.X > 120 && pick.Y < 110);
    }

    [Fact]
    public void AnAssemblyViewGetsOnlyItsOverallSizes()
    {
        ViewEntity[] entities =
        [
            new("edge#1", "line", 60, 80, 140, 80),
            new("edge#2", "line", 60, 120, 60, 80),
            new("edge#3", "circle", 100, 100, 110, 100),
            new("edge#4", "line", 70, 90, 100, 130),
        ];

        Assert.Equal(["edge#1", "edge#2"], DimensionPlan.Overall(View(), entities, []).Select(p => p.First));
    }

    [Fact]
    public void AHoleIsLocatedFromTheLeftAndBottomEdges()
    {
        ViewEntity[] entities =
        [
            new("edge#1", "line", 60, 80, 140, 80),
            new("edge#2", "line", 60, 120, 60, 80),
            new("edge#3", "line", 140, 80, 140, 120),
            new("edge#4", "circle", 80, 95, 84, 95),
        ];

        var located = DimensionPlan.Choose(View(), entities, [], []).Where(p => p.Second == "edge#4").Select(p => p.First).ToList();

        Assert.Equal(["edge#2", "edge#1"], located);
    }

    [Fact]
    public void AnEllipseIsNeverDimensioned()
    {
        Assert.Empty(DimensionPlan.Choose(View(), [new ViewEntity("edge#1", "ellipse", 100, 100, 110, 100)], [], []));
    }

    [Fact]
    public void ASizeAlreadyOnTheSheetIsLeftOut()
    {
        Assert.Empty(DimensionPlan.Choose(View(), [new ViewEntity("edge#1", "line", 60, 80, 140, 80)], [80], []));
    }

    [Fact]
    public void ABarsLengthRunsBetweenItsEnds()
    {
        ViewEntity[] entities = [new("edge#1", "line", 60, 95, 60, 105), new("edge#2", "line", 140, 95, 140, 105)];

        var pick = DimensionPlan.Choose(View(), entities, [], [])[0];

        Assert.Equal(("edge#1", "edge#2"), (pick.First, pick.Second));
    }

    [Fact]
    public void TextInTheWayMovesAndClearTextStays()
    {
        var border = new SheetBox(0, 0, 280, 210);
        SheetAnnotation[] text =
        [
            new("D1", 50, 50, false, new SheetBox(45, 48, 55, 52), "D1"),
            new("D2", 52, 50, false, new SheetBox(47, 48, 57, 52), "D2"),
        ];

        var moves = TextPlacement.Tidy(text, [], border);

        Assert.Equal("D2", Assert.Single(moves).Name);
    }

    [Fact]
    public void TextIsKeptInsideTheBorder()
    {
        var border = new SheetBox(10, 10, 100, 100);

        var move = Assert.Single(TextPlacement.Tidy([new SheetAnnotation("D1", 99, 50, false, new SheetBox(94, 48, 104, 52), "D1")], [], border));

        Assert.True(move.X + 5 <= 100);
    }

    [Fact]
    public void BalloonsSpreadRoundTheViewKeepingTheirOrder()
    {
        SheetAnnotation[] balloons =
        [
            new("1", 100, 110, false, null, "a"),
            new("2", 110, 100, false, null, "b"),
            new("3", 90, 100, false, null, "c"),
            new("4", 100, 90, false, null, "d"),
        ];

        var moves = TextPlacement.Ring(balloons, 100, 100, 50, 40);

        Assert.Equal(4, moves.Count);
        Assert.Equal(moves.Count, moves.Select(m => (Math.Round(m.X), Math.Round(m.Y))).Distinct().Count());
        Assert.All(moves, m => Assert.InRange(Math.Pow((m.X - 100) / 50, 2) + Math.Pow((m.Y - 100) / 40, 2), 0.99, 1.01));
    }
}
