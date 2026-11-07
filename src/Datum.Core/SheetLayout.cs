using System.Globalization;

namespace Datum.Core;

public enum ViewRole
{
    Main,
    Above,
    Beside,
    Pictorial,
    Other,
}

public sealed record LaidView(string Handle, ViewRole Role, double Width, double Height);

public sealed record ViewPlace(string Handle, double X, double Y);

public static class SheetLayout
{
    // Room around a view for its dimensions.
    private const double DimensionRoom = 20;
    private const double Gap = 30;
    private const double TitleClearance = 6;

    // Place puts views exactly DimensionRoom from the edge, so rounding must not count as running over.
    private const double FitTolerance = 0.01;

    private static readonly (double Numerator, double Denominator)[] Scales =
        [(5, 1), (2, 1), (1, 1), (1, 2), (1, 3), (1, 4), (1, 5), (1, 6), (1, 8), (1, 10), (1, 12), (1, 15), (1, 20), (1, 25), (1, 50), (1, 100)];

    public static SheetBox Usable(DrawingFacts facts)
    {
        var frame = facts.Frame ?? new SheetBox(0, 0, facts.WidthMm, facts.HeightMm);
        var title = facts.TitleBlock.Where(box => box.Top < facts.HeightMm / 2).Select(box => box.Top).DefaultIfEmpty(frame.Bottom).Max();
        return new SheetBox(frame.Left, title + TitleClearance, frame.Right, frame.Top);
    }

    // Views are measured full size. Returns the largest standard scale that fits.
    public static string ChooseScale(IReadOnlyList<LaidView> views, SheetBox usable)
    {
        var chosen = Scales.FirstOrDefault(s => Fits(views, s.Numerator / s.Denominator, usable), Scales[^1]);
        return string.Create(CultureInfo.InvariantCulture, $"{chosen.Numerator}:{chosen.Denominator}");
    }

    private static bool Fits(IReadOnlyList<LaidView> views, double ratio, SheetBox usable)
    {
        var sized = views.Select(v => v with { Width = v.Width * ratio, Height = v.Height * ratio }).ToList();
        var boxes = Place(sized, usable).Select(p =>
        {
            var view = sized.Single(v => v.Handle == p.Handle);
            return new SheetBox(p.X - (view.Width / 2), p.Y - (view.Height / 2), p.X + (view.Width / 2), p.Y + (view.Height / 2));
        }).ToList();
        return boxes.All(box => box.Inset(-DimensionRoom + FitTolerance).Within(usable))
            && !boxes.SelectMany((box, i) => boxes.Skip(i + 1).Select(other => box.Inset(-Gap / 2).Overlaps(other.Inset(-Gap / 2)))).Any(clash => clash);
    }

    // Main sits bottom left with the view above it over it, the view beside it to its right, and the pictorial top right.
    public static IReadOnlyList<ViewPlace> Place(IReadOnlyList<LaidView> views, SheetBox usable)
    {
        var leftWidth = views.Where(v => v.Role is ViewRole.Main or ViewRole.Above).Select(v => v.Width).DefaultIfEmpty(0).Max();
        var left = usable.Left + DimensionRoom + (leftWidth / 2);
        var main = views.FirstOrDefault(v => v.Role == ViewRole.Main);
        var bottom = usable.Bottom + DimensionRoom + ((main?.Height ?? 0) / 2);
        var rightColumn = usable.Left + DimensionRoom + leftWidth + Gap;
        var places = new List<ViewPlace>();
        var mainTop = bottom + ((main?.Height ?? 0) / 2);
        foreach (var view in views.Where(v => v.Role is ViewRole.Main or ViewRole.Above))
        {
            places.Add(new ViewPlace(view.Handle, left, view.Role == ViewRole.Main ? bottom : mainTop + Gap + (view.Height / 2)));
        }

        var beside = views.FirstOrDefault(v => v.Role == ViewRole.Beside);
        if (beside is not null)
        {
            places.Add(new ViewPlace(beside.Handle, rightColumn + (beside.Width / 2), bottom));
        }

        var top = usable.Top - DimensionRoom;
        foreach (var view in views.Where(v => v.Role is ViewRole.Pictorial or ViewRole.Other))
        {
            places.Add(new ViewPlace(view.Handle, usable.Right - DimensionRoom - (view.Width / 2), top - (view.Height / 2)));
            top -= view.Height + Gap;
        }

        return places;
    }

    // A projected view has no orientation name, and moves only along the line to its parent, so which side it's on says
    // what it is.
    public static ViewRole RoleOf(ViewFacts view, IReadOnlyList<ViewFacts> all)
    {
        var main = all.FirstOrDefault(v => v.Orientation == "front") ?? all.FirstOrDefault(v => v is { Isometric: false, View.Kind: "named" });
        return view switch
        {
            _ when view == main => ViewRole.Main,
            { View.Kind: "section" or "detail" or "auxiliary" } => ViewRole.Other,
            { Isometric: true } => ViewRole.Pictorial,
            { Orientation: "top" } => ViewRole.Above,
            { Orientation: "right" or "left" } => ViewRole.Beside,
            { View.Kind: "projected" } when main is not null =>
                Math.Abs(view.View.X - main.View.X) < Math.Abs(view.View.Y - main.View.Y) ? ViewRole.Above : ViewRole.Beside,
            _ => ViewRole.Other,
        };
    }
}

public sealed record DimensionPick(string First, string? Second, double X, double Y);

public static class DimensionPlan
{
    private const double Step = 11;
    private const double FirstRow = 12;
    private const double SideRoom = 16;
    private const double SmallestSize = 0.5;

    // Beyond the overall sizes, so a view stays readable.
    private const int MostEdges = 3;

    // Located holes per view; past this a drawing needs a hole table, which Datum doesn't make.
    private const int MostHoles = 6;
    private const double StraightTolerance = 0.05;

    // Overall sizes, then edges whose length is a size the model still needs (wanted, in sheet mm), which picks up
    // thicknesses, and sloped edges, whose line is the only place a slanted tube can be sized. Most edges end where
    // another happens to cross them, so their lengths mean nothing. Then true diameters. shown holds sizes already on
    // the sheet in sheet mm at this scale, so each size appears once. Edges seen as lines across a circle give a bar's
    // length.
    public static IReadOnlyList<DimensionPick> Choose(ViewInfo view, IReadOnlyList<ViewEntity> entities, IReadOnlyCollection<double> shown, IReadOnlyCollection<double> wanted)
    {
        var picks = new List<DimensionPick>();
        var taken = shown.ToList();
        bool Fresh(double size)
        {
            if (size < SmallestSize || taken.Any(known => Math.Abs(known - size) < StraightTolerance))
            {
                return false;
            }

            taken.Add(size);
            return true;
        }

        var straight = entities.Where(e => e.Kind == "line").ToList();
        var flat = straight.Where(e => Math.Abs(e.Y1 - e.Y2) < StraightTolerance).OrderByDescending(e => Math.Abs(e.X1 - e.X2)).FirstOrDefault();
        var upright = straight.Where(e => Math.Abs(e.X1 - e.X2) < StraightTolerance).OrderByDescending(e => Math.Abs(e.Y1 - e.Y2)).ToList();
        var across = upright.Count >= 2 ? (Low: upright.MinBy(e => e.X1)!, High: upright.MaxBy(e => e.X1)!) : default;
        var flatSpan = flat is null ? 0 : Math.Abs(flat.X1 - flat.X2);
        var barSpan = across.Low is null ? 0 : across.High.X1 - across.Low.X1;
        var top = view.Y + (view.Height / 2) + FirstRow;
        var side = view.X - (view.Width / 2) - SideRoom;
        if (barSpan > flatSpan + StraightTolerance && Fresh(barSpan))
        {
            picks.Add(new DimensionPick(across.Low!.Handle, across.High!.Handle, view.X, top));
        }
        else if (flat is not null && Fresh(flatSpan))
        {
            picks.Add(new DimensionPick(flat.Handle, null, view.X, top));
        }

        var tall = upright.FirstOrDefault();
        if (tall is not null && Fresh(Math.Abs(tall.Y1 - tall.Y2)))
        {
            picks.Add(new DimensionPick(tall.Handle, null, side, view.Y));
        }

        var edges = 0;
        var slopes = 0;
        var reach = FirstRow + Step;
        foreach (var edge in straight.OrderByDescending(Length))
        {
            if (edges == MostEdges)
            {
                break;
            }

            var level = Math.Abs(edge.Y1 - edge.Y2) < StraightTolerance;
            var sloped = !level && Math.Abs(edge.X1 - edge.X2) >= StraightTolerance;
            if ((sloped ? slopes > 0 : !wanted.Any(size => Math.Abs(size - Length(edge)) < StraightTolerance)) || !Fresh(Length(edge)))
            {
                continue;
            }

            slopes += sloped ? 1 : 0;

            picks.Add(level ? new DimensionPick(edge.Handle, null, (edge.X1 + edge.X2) / 2, view.Y + (view.Height / 2) + reach)
                : sloped ? OffTheEdge(view, edge)
                : new DimensionPick(edge.Handle, null, view.X + (view.Width / 2) + SideRoom + reach, (edge.Y1 + edge.Y2) / 2));
            reach += Step;
            edges++;
        }

        var row = view.Y + (view.Height / 2);
        foreach (var circle in entities.Where(e => e.Kind == "circle").OrderByDescending(e => Size(e)))
        {
            if (Fresh(Size(circle)))
            {
                picks.Add(new DimensionPick(circle.Handle, null, view.X + (view.Width / 2) + SideRoom, row));
                row -= Step;
            }
        }

        picks.AddRange(Locations(view, entities, across.Low, straight.Where(e => Math.Abs(e.Y1 - e.Y2) < StraightTolerance).MinBy(e => e.Y1)));

        return picks;
    }

    // Each hole's centre from the left and bottom edges of the view, once for each distinct distance, below and to the
    // right of the view, clear of the overall sizes above and to the left and the diameters just right of it.
    private static IEnumerable<DimensionPick> Locations(ViewInfo view, IReadOnlyList<ViewEntity> entities, ViewEntity? left, ViewEntity? bottom)
    {
        var across = new List<double>();
        var up = new List<double>();
        var below = view.Y - (view.Height / 2) - FirstRow;
        var beside = view.X + (view.Width / 2) + SideRoom + (2 * Step);
        foreach (var circle in entities.Where(e => e.Kind == "circle").Take(MostHoles))
        {
            if (left is not null && circle.X1 - left.X1 is var dx and > SmallestSize && !across.Any(known => Math.Abs(known - dx) < StraightTolerance))
            {
                across.Add(dx);
                yield return new DimensionPick(left.Handle, circle.Handle, (left.X1 + circle.X1) / 2, below);
                below -= Step;
            }

            if (bottom is not null && circle.Y1 - bottom.Y1 is var dy and > SmallestSize && !up.Any(known => Math.Abs(known - dy) < StraightTolerance))
            {
                up.Add(dy);
                yield return new DimensionPick(bottom.Handle, circle.Handle, beside, (bottom.Y1 + circle.Y1) / 2);
                beside += Step;
            }
        }
    }

    // Beside the middle of a sloped edge, on the side away from the view's centre.
    private static DimensionPick OffTheEdge(ViewInfo view, ViewEntity edge)
    {
        var (x, y) = ((edge.X1 + edge.X2) / 2, (edge.Y1 + edge.Y2) / 2);
        var (normalX, normalY) = ((edge.Y2 - edge.Y1) / Length(edge), (edge.X1 - edge.X2) / Length(edge));
        var outward = ((x - view.X) * normalX) + ((y - view.Y) * normalY) < 0 ? -1 : 1;
        return new DimensionPick(edge.Handle, null, x + (outward * FirstRow * normalX), y + (outward * FirstRow * normalY));
    }

    private static double Length(ViewEntity line) => Math.Sqrt(Math.Pow(line.X1 - line.X2, 2) + Math.Pow(line.Y1 - line.Y2, 2));

    // The longest level and upright edges, for a view of something made of many parts.
    public static IReadOnlyList<DimensionPick> Overall(ViewInfo view, IReadOnlyList<ViewEntity> entities, IReadOnlyCollection<double> shown) =>
        Choose(view, [.. entities.Where(e => e.Kind == "line" && (Math.Abs(e.X1 - e.X2) < StraightTolerance || Math.Abs(e.Y1 - e.Y2) < StraightTolerance))], shown, []);

    private static double Size(ViewEntity circle) => 2 * Math.Sqrt(Math.Pow(circle.X1 - circle.X2, 2) + Math.Pow(circle.Y1 - circle.Y2, 2));
}

public sealed record TextMove(string Name, double X, double Y);

public static class TextPlacement
{
    private const double Clearance = 2;
    private const double SearchStep = 6;
    private const int SearchRings = 24;

    // Moves each piece of text that's in the way to the nearest clear spot. Text that's already clear stays put.
    public static IReadOnlyList<TextMove> Tidy(IReadOnlyList<SheetAnnotation> text, IReadOnlyList<SheetBox> keepOut, SheetBox border)
    {
        var placed = new List<SheetBox>(keepOut);
        var moves = new List<TextMove>();
        foreach (var item in text.Where(t => t.Box is not null && t.Name is not null))
        {
            var box = item.Box!.Value;
            if (Clear(box, placed, border))
            {
                placed.Add(box);
                continue;
            }

            foreach (var (dx, dy) in Offsets())
            {
                var moved = new SheetBox(box.Left + dx, box.Bottom + dy, box.Right + dx, box.Top + dy);
                if (Clear(moved, placed, border))
                {
                    moves.Add(new TextMove(item.Name!, item.X + dx, item.Y + dy));
                    placed.Add(moved);
                    break;
                }
            }
        }

        return moves;
    }

    // Even spacing round an ellipse, in the order the items already sit, so leaders don't cross more than they must.
    public static IReadOnlyList<TextMove> Ring(IReadOnlyList<SheetAnnotation> balloons, double x, double y, double radiusX, double radiusY)
    {
        var ordered = balloons.Where(b => b.Name is not null).OrderBy(b => Math.Atan2(b.Y - y, b.X - x)).ToList();
        return
        [
            .. ordered.Select((balloon, i) =>
            {
                var (sin, cos) = Math.SinCos(-Math.PI + ((i + 0.5) * 2 * Math.PI / ordered.Count));
                return new TextMove(balloon.Name!, x + (radiusX * cos), y + (radiusY * sin));
            }),
        ];
    }

    private static bool Clear(SheetBox box, List<SheetBox> others, SheetBox border) =>
        box.Within(border) && !others.Any(other => box.Inset(-Clearance).Overlaps(other));

    private static IEnumerable<(double X, double Y)> Offsets()
    {
        for (var ring = 1; ring <= SearchRings; ring++)
        {
            for (var turn = 0; turn < 16; turn++)
            {
                var (sin, cos) = Math.SinCos(turn * Math.PI / 8);
                yield return (ring * SearchStep * cos, ring * SearchStep * sin);
            }
        }
    }
}
