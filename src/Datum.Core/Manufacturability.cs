using System.Globalization;

namespace Datum.Core;

// Thickness is how far a ray from the face goes through material before leaving it. Blocked means a ray from the face
// along the pull direction it faces hits the part again, so a two-part mould couldn't release that face.
public sealed record FaceProbe(string Handle, SurfaceKind Surface, Point3 Point, Point3 Normal, double? ThicknessMm, bool Blocked);

public sealed record SheetBends(double ThicknessMm, IReadOnlyList<(string Feature, double RadiusMm)> Bends);

public sealed record MakeLimits(string Process, Point3 Pull, double MinWallMm, double MinDraftDegrees);

public static class Manufacturability
{
    private static readonly string[] Processes = ["machining", "molding", "sheetmetal"];

    public static void Validate(MakeLimits limits)
    {
        if (!Processes.Contains(limits.Process))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"unknown process '{limits.Process}'", $"Use {string.Join(", ", Processes)}");
        }

        if (limits.MinWallMm <= 0 || limits.MinDraftDegrees < 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "min_wall must be above 0 and min_draft 0 or more", "Nothing was checked");
        }
    }

    public static IReadOnlyList<string> Find(IReadOnlyList<FaceProbe> faces, SheetBends? sheet, MakeLimits limits)
    {
        var problems = new List<string>();
        problems.AddRange(faces.Where(f => f.ThicknessMm < limits.MinWallMm)
            .Select(f => string.Create(CultureInfo.InvariantCulture, $"{f.Handle}: wall {Numbers.Format(f.ThicknessMm!.Value)} mm, under {Numbers.Format(limits.MinWallMm)} mm")));
        if (limits.Process == "molding")
        {
            var pull = Unit(limits.Pull);
            foreach (var face in faces)
            {
                var draft = 90 - (Math.Acos(Math.Clamp(Math.Abs(Dot(Unit(face.Normal), pull)), 0, 1)) * 180 / Math.PI);
                if (draft < limits.MinDraftDegrees - 1e-6)
                {
                    problems.Add(string.Create(CultureInfo.InvariantCulture, $"{face.Handle}: draft {draft:0.0}°, under {Numbers.Format(limits.MinDraftDegrees)}°"));
                }
            }

            problems.AddRange(faces.Where(f => f.Blocked).Select(f => $"{f.Handle}: undercut, the part is in the way of pulling it out"));
        }

        if (limits.Process == "sheetmetal" && sheet is not null)
        {
            problems.AddRange(sheet.Bends.Where(b => b.RadiusMm < sheet.ThicknessMm - 1e-6)
                .Select(b => string.Create(CultureInfo.InvariantCulture, $"{b.Feature}: bend radius {Numbers.Format(b.RadiusMm)} mm is under the {Numbers.Format(sheet.ThicknessMm)} mm thickness and may crack")));
        }

        return problems;
    }

    public static Point3 Unit(Point3 v)
    {
        var length = Math.Sqrt(Dot(v, v));
        return length == 0 ? v : new Point3(v.X / length, v.Y / length, v.Z / length);
    }

    private static double Dot(Point3 a, Point3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);
}
