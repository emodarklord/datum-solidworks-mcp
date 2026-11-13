using System.ComponentModel;

namespace Datum.Core;

// Where a component sits in its assembly, in mm and degrees. Rotations apply about the assembly's X, then Y, then Z.
public readonly record struct ComponentPose(Point3 PositionMm, Point3 RotationDegrees)
{
    private const double Mm = 1000;
    private const double GimbalLimit = 1 - 1e-9;

    public bool IsRotated => RotationDegrees.Length > 1e-6;

    // SOLIDWORKS's math transforms hold each axis's image as a row, then the translation in metres, then a scale of 1.
    public double[] ToTransform()
    {
        var (sx, cx) = SinCos(RotationDegrees.X);
        var (sy, cy) = SinCos(RotationDegrees.Y);
        var (sz, cz) = SinCos(RotationDegrees.Z);
        return
        [
            cz * cy, sz * cy, -sy,
            (cz * sy * sx) - (sz * cx), (sz * sy * sx) + (cz * cx), cy * sx,
            (cz * sy * cx) + (sz * sx), (sz * sy * cx) - (cz * sx), cy * cx,
            PositionMm.X / Mm, PositionMm.Y / Mm, PositionMm.Z / Mm,
            1, 0, 0, 0,
        ];
    }

    public static ComponentPose FromTransform(double[] transform)
    {
        var position = new Point3(transform[9] * Mm, transform[10] * Mm, transform[11] * Mm);
        var sinY = -transform[2];
        double x, z;
        if (Math.Abs(sinY) < GimbalLimit)
        {
            x = Math.Atan2(transform[5], transform[8]);
            z = Math.Atan2(transform[1], transform[0]);
        }
        else
        {
            x = Math.Atan2(-transform[7], transform[4]);
            z = 0;
        }

        return new ComponentPose(position, new Point3(Degrees(x), Degrees(Math.Asin(Math.Clamp(sinY, -1, 1))), Degrees(z)));
    }

    // In the assembly's coordinates, from the component's own. Mm in, mm out.
    public Point3 Place(Point3 mm)
    {
        var t = ToTransform();
        return new Point3(
            (mm.X * t[0]) + (mm.Y * t[3]) + (mm.Z * t[6]) + PositionMm.X,
            (mm.X * t[1]) + (mm.Y * t[4]) + (mm.Z * t[7]) + PositionMm.Y,
            (mm.X * t[2]) + (mm.Y * t[5]) + (mm.Z * t[8]) + PositionMm.Z);
    }

    public Point3 Turn(Point3 direction)
    {
        var t = ToTransform();
        return new Point3(
            (direction.X * t[0]) + (direction.Y * t[3]) + (direction.Z * t[6]),
            (direction.X * t[1]) + (direction.Y * t[4]) + (direction.Z * t[7]),
            (direction.X * t[2]) + (direction.Y * t[5]) + (direction.Z * t[8]));
    }

    public bool Matches(ComponentPose other) =>
        new Point3(PositionMm.X - other.PositionMm.X, PositionMm.Y - other.PositionMm.Y, PositionMm.Z - other.PositionMm.Z).Length < 1e-4
        && ToTransform().Take(9).Zip(other.ToTransform().Take(9)).All(pair => Math.Abs(pair.First - pair.Second) < 1e-6);

    public override string ToString() => IsRotated ? $"at {PositionMm} mm, rotated {RotationDegrees}°" : $"at {PositionMm} mm";

    private static (double Sin, double Cos) SinCos(double degrees) => Math.SinCos(degrees * Math.PI / 180);

    private static double Degrees(double radians)
    {
        var degrees = radians * 180 / Math.PI;
        return Math.Abs(degrees) < 1e-9 ? 0 : degrees;
    }
}

public sealed record ComponentInfo(string Handle, string Name, string? Path, string Configuration, bool Fixed, bool Suppressed, string Status, ComponentPose Pose)
{
    // A sub-assembly whose own mates still move inside the main one, as a mechanism needs.
    public bool Flexible { get; init; }

    public override string ToString()
    {
        var state = Suppressed ? "suppressed" : Fixed ? "fixed" : Status;
        return $"{Handle} {Name} [{Configuration}] {state}{(Flexible ? ", flexible" : "")}, {Pose}";
    }
}

// Held means mates kept the component from reaching the requested pose.
public sealed record ComponentMove(ComponentPose Before, ComponentPose After, bool Held);

public sealed record MateSpec(string Kind, string First, string Second, double? Value, string Alignment, bool Flip)
{
    private static readonly string[] Kinds = ["coincident", "concentric", "parallel", "perpendicular", "tangent", "distance", "angle", "lock", "gear", "screw", "profile_center"];

    public string NormalizedKind => Kind.Trim().ToLowerInvariant();

    // Limits turn a distance or angle mate into a range, with Value where it starts.
    public double? Min { get; init; }

    public double? Max { get; init; }

    // Gear only, as first:second teeth or diameters, e.g. 60:13.
    public string? Ratio { get; init; }

    // Profile center only: stop the second profile turning about the first.
    public bool LockRotation { get; init; }

    public string Label => Ratio is null ? Kind : $"{Kind} {Ratio}";

    public bool Limited => Min is not null;

    public string NormalizedAlignment => Alignment.Trim().ToLowerInvariant();

    public void Validate()
    {
        if (!Kinds.Contains(NormalizedKind))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown mate '{Kind}'", $"Use {string.Join(", ", Kinds)}");
        }

        if (NormalizedAlignment is not ("closest" or "aligned" or "anti_aligned"))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown alignment '{Alignment}'", "Use closest, aligned or anti_aligned");
        }

        var screw = NormalizedKind == "screw";
        if (screw && Value is not > 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "a screw mate needs its lead, in mm per turn, above 0", "Pass it as value");
        }

        var needsValue = screw || NormalizedKind is "distance" or "angle";
        var center = NormalizedKind == "profile_center";
        if (center && Value is < 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"a {NormalizedKind} mate's value can't be negative", "Use flip for the other side");
        }

        if (needsValue && Value is not >= 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"a {NormalizedKind} mate needs a value of 0 or more", "Nothing was changed");
        }

        if (!needsValue && !center && Value is not null)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"a {NormalizedKind} mate takes no value", "Nothing was changed");
        }

        if (LockRotation && !center)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "lock_rotation goes with a profile_center mate", "Nothing was changed");
        }

        if (Min is null != Max is null || (Min is not null && (!needsValue || screw || Min < 0 || Min >= Max || Value < Min || Value > Max)))
        {
            throw new DatumException(ErrorCode.InvalidArgument, "limits need a distance or angle mate with min and max, 0 <= min < max, and the value between them", "Nothing was changed");
        }

        if (NormalizedKind == "gear" ? Ratio is null : Ratio is not null)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "a ratio goes with a gear mate, and a gear mate needs one", "Write it like 60:13");
        }

        if (Ratio is not null)
        {
            _ = DrawingScale.Parse(Ratio);
        }
    }
}

public sealed record ComponentMoved(string Handle, string Name, ComponentPose Before, ComponentPose After);

// Unconstrained lists the components still free to move.
public sealed record MateResult(string Handle, string Name, IReadOnlyList<ComponentMoved> Moved, IReadOnlyList<string> Unconstrained);

public sealed record MateInfo(string Handle, string Name, string Kind, IReadOnlyList<string> Components, bool Suppressed, string? Error)
{
    // Gear and screw mates only, such as 4:1 reversed or 4 mm/turn.
    public string? Ratio { get; init; }

    public string Label => Ratio is null ? Kind : $"{Kind} {Ratio}";
}

public sealed record InterferencePair(IReadOnlyList<string> Components, double VolumeMm3);

public sealed record BomLine(string File, string Configuration, int Quantity, string? Description, string? PartNumber);

public static class AssemblyProblems
{
    private const string CanMove = ", so it can still move";

    // A solver failure, not freedom: the part sits wherever the solver gave up.
    private static readonly string[] Unsolved = ["no solution", "over defined", "invalid solution"];

    public static bool IsFreeToMove(string problem) => problem.EndsWith(CanMove, StringComparison.Ordinal);

    public static IReadOnlyList<string> Find(
        IReadOnlyList<ComponentInfo> components, IReadOnlyList<MateInfo> mates, IReadOnlyList<InterferencePair> interferences, IReadOnlyList<string> missingFiles)
    {
        var problems = new List<string>();
        problems.AddRange(missingFiles.Select(file => $"Missing file: {file}"));
        problems.AddRange(interferences.Select(pair => $"{string.Join(" and ", pair.Components)} overlap by {Numbers.Format(pair.VolumeMm3)} mm³"));
        problems.AddRange(mates.Where(m => m.Error is not null).Select(m => $"{m.Name} has an error: {m.Error}"));
        problems.AddRange(components.Where(c => c.Suppressed).Select(c => $"{c.Name} is suppressed"));
        problems.AddRange(components.Where(c => !c.Suppressed && Unsolved.Contains(c.Status))
            .Select(c => $"{c.Name} is {c.Status}: its mates can't all be met, so it isn't where they say. Hint: mate_list shows which mates conflict"));
        problems.AddRange(components.Where(c => !c.Suppressed && !c.Fixed && c.Status != "fully defined" && !Unsolved.Contains(c.Status)).Select(c => $"{c.Name} is {c.Status}{CanMove}"));
        if (components.Count > 0 && !components.Any(c => c.Fixed))
        {
            problems.Add("No component is fixed, so the whole assembly can move");
        }

        return problems;
    }
}

public sealed record ConfigurationInfo(string Name, bool Active);

public sealed record ExplodeStepSpec
{
    [Description("Component to move, comp#N")]
    public string Component { get; init; } = "";

    [Description("x, y or z")]
    public string Axis { get; init; } = "";

    [Description("Distance in mm; negative goes the other way along the axis")]
    public double Distance { get; init; }

    public static void Validate(IReadOnlyList<ExplodeStepSpec> steps)
    {
        if (steps.Count == 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "an exploded view needs at least one step", "Nothing was changed");
        }

        foreach (var step in steps)
        {
            if (!step.Component.StartsWith("comp#", StringComparison.OrdinalIgnoreCase))
            {
                throw new DatumException(ErrorCode.InvalidArgument, $"'{step.Component}' is not a comp# handle", "Nothing was changed");
            }

            if (step.Axis.Trim().ToUpperInvariant() is not ("X" or "Y" or "Z"))
            {
                throw new DatumException(ErrorCode.InvalidArgument, $"unknown axis '{step.Axis}'", "Use x, y or z");
            }

            if (step.Distance == 0)
            {
                throw new DatumException(ErrorCode.InvalidArgument, $"{step.Component} needs a distance other than 0", "Nothing was changed");
            }
        }
    }
}

public sealed record ExplodeStepInfo(string Name, string Component, double DistanceMm);

public sealed record ExplodeResult(string View, string Configuration, IReadOnlyList<ExplodeStepInfo> Steps);
