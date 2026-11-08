using System.Globalization;

namespace Datum.Core;

public sealed record DrawingSpec(string Sheet, string Scale, string Projection, string Units = "mm")
{
    // Numbered like swDwgPaperSizes_e.
    private static readonly string[] Sizes = ["a", "a-portrait", "b", "c", "d", "e", "a4", "a4-portrait", "a3", "a2", "a1", "a0"];

    public string SheetName => Sheet.Trim().ToLowerInvariant();

    public int PaperSize => Array.IndexOf(Sizes, SheetName);

    public bool FirstAngle => Projection.Trim().Equals("first", StringComparison.OrdinalIgnoreCase);

    public (double Numerator, double Denominator) ParsedScale => DrawingScale.Parse(Scale);

    public void Validate()
    {
        if (PaperSize < 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown sheet size '{Sheet}'", $"Use {string.Join(", ", Sizes)}");
        }

        if (!FirstAngle && !Projection.Trim().Equals("third", StringComparison.OrdinalIgnoreCase))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown projection '{Projection}'", "Use first or third");
        }

        if (Units.Trim().ToLowerInvariant() is not ("mm" or "inches"))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown units '{Units}'", "Use mm or inches");
        }

        _ = ParsedScale;
    }
}

public static class DrawingScale
{
    public static (double Numerator, double Denominator) Parse(string scale)
    {
        var parts = scale.Split(':');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
            && numerator > 0 && denominator > 0)
        {
            return (numerator, denominator);
        }

        throw new DatumException(ErrorCode.InvalidArgument, $"unknown scale '{scale}'", "Write it like 1:2 or 2:1");
    }
}

public static class ViewDisplays
{
    // Numbered like swDisplayMode_e.
    public static int Parse(string display) => display.Trim().ToLowerInvariant() switch
    {
        "wireframe" => 0,
        "hidden" => 1,
        "removed" => 2,
        "shaded" => 7,
        _ => throw new DatumException(ErrorCode.InvalidArgument, $"unknown display '{display}'", "Use hidden (hidden lines shown), removed (hidden lines removed), wireframe or shaded"),
    };

    public static string Name(int mode) => mode switch
    {
        0 or 4 => "wireframe",
        1 or 5 => "hidden",
        2 or 6 => "removed",
        3 or 7 => "shaded",
        _ => "default",
    };
}

public sealed record ViewInfo(
    string Handle, string Name, string Sheet, string Kind, string? Model, string Scale, double X, double Y, double Width, double Height, string Display, int Dimensions, int CentreMarks, int CentreLines)
{
    public override string ToString() =>
        $"{Handle} {Name} [{Kind}] {Scale} at {Numbers.Format(X)},{Numbers.Format(Y)} mm, {Numbers.Format(Width)} x {Numbers.Format(Height)} mm, {Display}{(Exploded is null ? "" : $", exploded {Exploded}")}, {Dimensions} dimensions, {CentreMarks} centre marks, {CentreLines} centre lines";

    // The configuration shown exploded. Null when the view isn't.
    public string? Exploded { get; init; }
}

// A null format means the sheet has no title block.
public sealed record DrawingCreated(DocumentInfo Document, string? Format, double WidthMm, double HeightMm, bool FirstAngle);

// A circle, arc or ellipse gives its centre, then a point on it. An ellipse is a circle seen at an angle, so its size
// on the sheet isn't its true size.
public sealed record ViewEntity(string Handle, string Kind, double X1, double Y1, double X2, double Y2)
{
    public override string ToString() => Kind is "circle" or "arc" or "ellipse"
        ? $"{Handle} {Kind} centre {Numbers.Format(X1)},{Numbers.Format(Y1)}, through {Numbers.Format(X2)},{Numbers.Format(Y2)}"
        : $"{Handle} {Kind} {Numbers.Format(X1)},{Numbers.Format(Y1)} to {Numbers.Format(X2)},{Numbers.Format(Y2)}";
}

public sealed record SheetDimension(string View, string Name, double Value, string Unit, bool Dangling, string? Tolerance)
{
    public override string ToString() => $"{View} {Name} {Numbers.Format(Value)}{Unit}{(Tolerance is null ? "" : $" {Tolerance}")}{(Dangling ? " DANGLING" : "")}";
}

// ModelPath is null for a dimension that belongs to the drawing, not the model.
public sealed record ToleranceChange(string Dimension, string Kind, double Upper, double Lower, string? ModelPath);

// Cylinders as a view sees them: end-on ones want a centre mark, side-on ones a centre line.
public sealed record MarkCount(string View, int CentreMarks, int CentreLines, int MissingMarks, int MissingLines);

public static class Tolerances
{
    // Numbered like swTolType_e.
    public static int Code(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "none" => 0,
        "bilateral" => 2,
        "limit" => 3,
        "symmetric" => 4,
        _ => throw new DatumException(ErrorCode.InvalidArgument, $"unknown tolerance '{kind}'", "Use bilateral, symmetric, limit or none"),
    };
}

public readonly record struct SheetBox(double Left, double Bottom, double Right, double Top)
{
    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Bottom && y <= Top;

    public bool Overlaps(SheetBox other) => Left < other.Right && Right > other.Left && Bottom < other.Top && Top > other.Bottom;

    public bool Within(SheetBox other) => Left >= other.Left && Right <= other.Right && Bottom >= other.Bottom && Top <= other.Top;

    public SheetBox Inset(double by) => new(Left + by, Bottom + by, Right - by, Top - by);

    // Clips the segment to the box, so a line that only passes through counts as well as one that ends inside.
    public bool Crossed(SheetSegment line)
    {
        var (start, end) = (0.0, 1.0);
        var (dx, dy) = (line.X2 - line.X1, line.Y2 - line.Y1);
        foreach (var (p, q) in new[] { (-dx, line.X1 - Left), (dx, Right - line.X1), (-dy, line.Y1 - Bottom), (dy, Top - line.Y1) })
        {
            if (p == 0)
            {
                if (q < 0)
                {
                    return false;
                }

                continue;
            }

            var t = q / p;
            if (p < 0)
            {
                start = Math.Max(start, t);
            }
            else
            {
                end = Math.Min(end, t);
            }
        }

        return start <= end;
    }
}

public readonly record struct SheetSegment(double X1, double Y1, double X2, double Y2);

// Box is the text's extent on the sheet. Name is what annotation_move and annotation_delete take; View is null on a sheet note.
public sealed record SheetAnnotation(string Text, double X, double Y, bool Dangling, SheetBox? Box = null, string? Name = null, string? View = null)
{
    // A dimension's own dimension and extension lines, which other text should keep clear of.
    public IReadOnlyList<SheetSegment> Lines { get; init; } = [];

    // What a reader sees, where it differs from Text: a dimension's value.
    public string? Shows { get; init; }

    public override string ToString() => $"{Name} {View ?? "sheet"} '{Shows ?? Text}' at {Numbers.Format(X)},{Numbers.Format(Y)}{(Dangling ? " DANGLING" : "")}";
}

public sealed record ViewFacts(ViewInfo View, SheetBox Outline, bool Isometric, int SideOnHoles, int MissingMarks, int MissingLines, IReadOnlyList<SheetAnnotation> Annotations)
{
    // The model's own extent on the sheet, without the balloons and text that can stretch the outline.
    public SheetBox? Geometry { get; init; }

    // front, top, right and so on, in lower case. Empty for a view with no named orientation.
    public string Orientation { get; init; } = "";
}

// Position marks a dimension from the sketch origin or a plane's offset: where a feature sits in the model, which a
// drawing usually gives another way.
public sealed record ModelDimension(string Name, double Value, string Unit, bool Position = false);

public sealed record DrawingFacts(
    double WidthMm,
    double HeightMm,
    IReadOnlyList<ViewFacts> Views,
    IReadOnlyList<ModelDimension> ModelDimensions,
    IReadOnlyList<SheetDimension> Shown,
    IReadOnlyList<string> MissingModels,
    bool NeedsRebuild)
{
    // Null on a part drawing.
    public IReadOnlyList<BomRow>? BomRows { get; init; }

    public IReadOnlyList<BomLine>? ActualBom { get; init; }

    public IReadOnlyList<BalloonInfo>? Balloons { get; init; }

    public SheetBox? BomBox { get; init; }

    // The sheet format's border. Null when the sheet has no format.
    public SheetBox? Frame { get; init; }

    // The format's lines inside the border, drawn as thin boxes, and its text. Together they are the title block.
    public IReadOnlyList<SheetBox> TitleBlock { get; init; } = [];

    public IReadOnlyList<SheetAnnotation> SheetNotes { get; init; } = [];

    public string? SheetScale { get; init; }

    // mm or inches: what the dimensions show, and what the sheet format's text claims. The second is null when it doesn't say.
    public string Units { get; init; } = "mm";

    public string? FormatUnits { get; init; }
}

public sealed record TitleField
{
    [System.ComponentModel.Description("Field name, e.g. DrawnBy, Description, Revision")]
    public string Name { get; init; } = "";

    [System.ComponentModel.Description("The text to show")]
    public string Value { get; init; } = "";
}

// ModelPath is set when the field belongs to the model, which then needs saving.
public sealed record TitleChange(string Name, string Value, bool Shown, string? ModelPath);

public sealed record BomRow(string Item, string PartNumber, string Description, int Quantity);

public sealed record BalloonInfo(string Item, string? Component, bool Dangling);

public sealed record ExplodeLine(string Component, Point3 From, Point3 To)
{
    public override string ToString() =>
        $"{Component}: {Numbers.Format(From.X)}, {Numbers.Format(From.Y)}, {Numbers.Format(From.Z)} to {Numbers.Format(To.X)}, {Numbers.Format(To.Y)}, {Numbers.Format(To.Z)} mm";
}

public static class DrawingAudit
{
    private const double ValueTolerance = 1e-3;
    private const double SheetTolerance = 0.5;
    private const double TextOverlap = 0.2;

    public static readonly string[] Skippable = ["dimensions", "marks", "bom"];

    // skip leaves whole checks out, for drawings where they don't apply: a frame built from sweeps has modelling
    // dimensions nobody cuts to, and tube ends that aren't holes. Report says what was left out.
    public static IReadOnlyList<string> Find(DrawingFacts facts, IReadOnlyList<string>? skip = null)
    {
        var (checkMarks, checkDimensions) = (skip?.Contains("marks") != true, skip?.Contains("dimensions") != true);
        var problems = new List<string>();
        if (facts.NeedsRebuild)
        {
            problems.Add("The drawing needs a rebuild");
        }

        problems.AddRange(facts.MissingModels.Select(model => $"Missing model: {model}"));
        if (facts.Views.Count == 0)
        {
            problems.Add("The drawing has no views");
        }

        foreach (var view in facts.Views)
        {
            var name = view.View.Name;
            if (view.View.Display == "removed" && view.SideOnHoles > 0 && !view.Isometric && view.View.Kind is not ("section" or "detail"))
            {
                problems.Add($"{name} hides {view.SideOnHoles} hole {(view.SideOnHoles == 1 ? "axis" : "axes")} from view: hidden lines are off, so the hole doesn't show. Use view_set with display hidden");
            }

            // A detail repeats its parent's holes, and SOLIDWORKS can't put marks on it.
            if (checkMarks && view.MissingMarks > 0 && view.View.Kind != "detail")
            {
                problems.Add($"{name} has {view.MissingMarks} hole {(view.MissingMarks == 1 ? "circle" : "circles")} with no centre mark. Use marks_add");
            }

            if (checkMarks && view.MissingLines > 0 && view.View.Kind != "detail")
            {
                problems.Add($"{name} has {view.MissingLines} side-on {(view.MissingLines == 1 ? "hole" : "holes")} with no centre line. Use marks_add");
            }

            problems.AddRange(view.Annotations.Where(a => a.Dangling).Select(a => $"{name}: '{a.Text}' is attached to nothing"));
            if (!view.Outline.Within(Border(facts).Inset(-SheetTolerance)))
            {
                problems.Add($"{name} runs off the sheet");
            }

            if (OnTitleBlock(facts, view.Outline))
            {
                problems.Add($"{name} overlaps the title block");
            }
        }

        problems.AddRange(TextProblems(facts));

        for (var i = 0; i < facts.Views.Count; i++)
        {
            for (var j = i + 1; j < facts.Views.Count; j++)
            {
                if (facts.Views[i].Outline.Overlaps(facts.Views[j].Outline))
                {
                    problems.Add($"{facts.Views[i].View.Name} overlaps {facts.Views[j].View.Name}");
                }
            }
        }

        if (facts.BomBox is { } bom)
        {
            if (!bom.Within(Border(facts).Inset(-SheetTolerance)))
            {
                problems.Add("The BOM runs off the sheet");
            }

            problems.AddRange(facts.Views.Where(v => (v.Geometry ?? v.Outline).Overlaps(bom)).Select(v => $"The BOM overlaps {v.View.Name}"));
        }

        if (facts.SheetScale is { } sheetScale && facts.TitleBlock.Count > 0 && facts.Views.FirstOrDefault(v => !v.Isometric && v.View.Kind != "detail") is { } main && main.View.Scale != sheetScale)
        {
            problems.Add($"The title block says {sheetScale}, but {main.View.Name} is {main.View.Scale}. Use sheet_set");
        }

        if (facts.FormatUnits is { } claimed && claimed != facts.Units)
        {
            problems.Add($"The title block says dimensions are in {claimed}, but they show {facts.Units}. Use sheet_set with units");
        }

        if (checkDimensions)
        {
            problems.AddRange(MissingDimensions(facts).Select(d => $"Not shown: {d.Name} {Numbers.Format(d.Value)}{d.Unit}"));
        }

        if (skip?.Contains("bom") != true)
        {
            problems.AddRange(BomProblems(facts));
        }
        return problems;
    }

    // A size is given once on a drawing, so one dimension covers every model dimension of that value. Report says how
    // many were covered that way, since two different features sharing a size can't be told apart here.
    public static IReadOnlyList<ModelDimension> MissingDimensions(DrawingFacts facts) =>
        [.. Unnamed(facts).Where(model => !SparesFor(facts, model).Any())];

    private static IEnumerable<ModelDimension> Unnamed(DrawingFacts facts) =>
        facts.ModelDimensions.Where(model => NeedsShowing(model) && !facts.Shown.Any(shown => SameName(shown, model)));

    // A revolve's sketch holds a radius, and the drawing shows it as a diameter.
    private static IEnumerable<SheetDimension> SparesFor(DrawingFacts facts, ModelDimension model) =>
        facts.Shown.Where(shown => !facts.ModelDimensions.Any(known => SameName(shown, known)) && shown.Unit == model.Unit
            && (Math.Abs(shown.Value - model.Value) < ValueTolerance || Math.Abs(shown.Value - (2 * model.Value)) < ValueTolerance));

    public static IEnumerable<string> OffSheet(DrawingFacts facts) =>
        facts.Views.Where(v => !v.Outline.Within(Border(facts).Inset(-SheetTolerance))).Select(v => v.View.Name);

    public static string Report(string title, DrawingFacts facts, IReadOnlyList<string>? skip = null)
    {
        var problems = Find(facts, skip);
        var sizes = facts.ModelDimensions.Count(NeedsShowing);
        var report = new System.Text.StringBuilder($"{title}: {facts.Views.Count} views, {sizes - MissingDimensions(facts).Count} of {sizes} model sizes shown\n");
        report.Append(problems.Count == 0 ? "No problems found\n" : $"Problems ({problems.Count}):\n");
        foreach (var problem in problems)
        {
            report.Append("- ").Append(problem).Append('\n');
        }

        if (skip?.Contains("dimensions") != true)
        {
            if (facts.ModelDimensions.Count(d => d.Position) is > 0 and var located)
            {
                report.Append(CultureInfo.InvariantCulture, $"NOT CHECKED: {located} positions measured from the origin or a plane. Make sure the sheet locates those features\n");
            }

            var repeated = Unnamed(facts).GroupBy(model => SparesFor(facts, model).FirstOrDefault()).Where(group => group.Key is not null).Sum(group => group.Count() - 1);
            if (repeated > 0)
            {
                report.Append(CultureInfo.InvariantCulture, $"NOT CHECKED: {repeated} sizes share a value with another, so one dimension on the sheet stands for them\n");
            }
        }

        if (skip is { Count: > 0 })
        {
            report.Append("NOT CHECKED, as asked: ").AppendJoin(", ", skip).Append('\n');
        }

        return report.Append("These checks passing doesn't prove the drawing meets its requirements, and they don't see every overlap in a tight gap. Look at the sheet.\n").ToString();
    }

    // A full revolve's 360 and a pattern's count are settings, not sizes anyone machines to.
    private static bool NeedsShowing(ModelDimension model) =>
        !model.Position && !(model.Unit == "°" && Math.Abs(model.Value - 360) < ValueTolerance) && !model.Name.Contains("Pattern", StringComparison.OrdinalIgnoreCase);

    private static bool SameName(SheetDimension shown, ModelDimension model) => string.Equals(shown.Name, model.Name, StringComparison.OrdinalIgnoreCase);

    private static SheetBox Border(DrawingFacts facts) => facts.Frame ?? new SheetBox(0, 0, facts.WidthMm, facts.HeightMm);

    private static bool OnTitleBlock(DrawingFacts facts, SheetBox box) => facts.TitleBlock.Any(box.Inset(TextOverlap).Overlaps);

    private static IEnumerable<string> TextProblems(DrawingFacts facts)
    {
        var placed = facts.Views.SelectMany(v => v.Annotations.Select(a => (Annotation: a, Owner: (ViewFacts?)v)))
            .Concat(facts.SheetNotes.Select(a => (Annotation: a, Owner: (ViewFacts?)null)))
            .Where(item => item.Annotation.Box is not null)
            .Select(item => (item.Annotation.Text, Box: item.Annotation.Box!.Value, item.Owner, item.Annotation.Lines))
            .ToList();
        foreach (var (text, box, owner, _) in placed)
        {
            if (placed.FirstOrDefault(other => other.Text != text && other.Lines.Any(box.Inset(TextOverlap).Crossed)) is { Text: { } crossing })
            {
                yield return $"'{text}' has a line of '{crossing}' through it";
            }

            var label = owner is null ? "Note" : owner.View.Name;
            if (!box.Within(Border(facts).Inset(-SheetTolerance)))
            {
                yield return $"{label}: '{text}' runs past the border";
            }

            if (OnTitleBlock(facts, box))
            {
                yield return $"{label}: '{text}' sits on the title block";
            }

            if (facts.BomBox is { } bom && box.Inset(TextOverlap).Overlaps(bom))
            {
                yield return $"{label}: '{text}' sits on the BOM";
            }

            foreach (var other in facts.Views.Where(v => v != owner && box.Inset(TextOverlap).Overlaps(v.Outline)))
            {
                yield return $"{label}: '{text}' sits on {other.View.Name}";
            }
        }

        for (var i = 0; i < placed.Count; i++)
        {
            for (var j = i + 1; j < placed.Count; j++)
            {
                if (placed[i].Box.Inset(TextOverlap).Overlaps(placed[j].Box.Inset(TextOverlap)))
                {
                    yield return $"'{placed[i].Text}' overlaps '{placed[j].Text}'";
                }
            }
        }
    }

    private static IEnumerable<string> BomProblems(DrawingFacts facts)
    {
        if (facts.ActualBom is { } actual)
        {
            if (facts.BomRows is not { } rows)
            {
                yield return "An assembly drawing with no BOM table";
            }
            else
            {
                foreach (var line in actual)
                {
                    var stem = Path.GetFileNameWithoutExtension(line.File);
                    var row = rows.FirstOrDefault(r => string.Equals(r.PartNumber, stem, StringComparison.OrdinalIgnoreCase) || string.Equals(r.PartNumber, line.PartNumber, StringComparison.OrdinalIgnoreCase));
                    if (row is null)
                    {
                        yield return $"The BOM has no row for {stem}";
                    }
                    else if (row.Quantity != line.Quantity)
                    {
                        yield return $"The BOM shows {row.Quantity} of {stem}, but the assembly has {line.Quantity}";
                    }
                }
            }
        }

        if (facts.Balloons is { } balloons && facts.BomRows is { } table)
        {
            foreach (var balloon in balloons.Where(b => b.Dangling || b.Component is null))
            {
                yield return $"Balloon {balloon.Item} points at nothing";
            }

            foreach (var row in table.Where(r => !balloons.Any(b => b.Item == r.Item)))
            {
                yield return $"BOM item {row.Item} ({row.PartNumber}) has no balloon";
            }

            foreach (var balloon in balloons.Where(b => b.Component is not null))
            {
                var row = table.FirstOrDefault(r => r.Item == balloon.Item);
                if (row is null)
                {
                    yield return $"Balloon {balloon.Item} matches no BOM item";
                }
                else if (!balloon.Component!.StartsWith(row.PartNumber, StringComparison.OrdinalIgnoreCase))
                {
                    yield return $"Balloon {balloon.Item} points at {balloon.Component}, but item {balloon.Item} is {row.PartNumber}";
                }
            }
        }
    }
}
