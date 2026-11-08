using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    // Rays start this far off the face so they don't hit it; the wall reading adds it back.
    private const double RayStart = 1e-5;
    private const int RayEntryAndExit = (int)(swRayPtsOpts_e.swRayPtsOptsENTRY_EXIT | swRayPtsOpts_e.swRayPtsOptsNORMALS);
    private const int RayHitValues = 9;

    // Points tried on each face, at its largest render triangles: one point can land where a curved face happens to be
    // fine, like the side of a hole.
    private const int SamplesPerFace = 6;

    public IReadOnlyList<FaceProbe> ProbeFaces(string id, Point3 pull) => Call<IReadOnlyList<FaceProbe>>(() =>
    {
        var document = Find(id);
        var bodies = (((IPartDoc)document).GetBodies2((int)swBodyType_e.swSolidBody, true) as object[] ?? []).OfType<IBody2>().ToArray();
        var faces = bodies.SelectMany(body => (body.GetFaces() as object[] ?? []).OfType<IFace2>()).ToList();
        var direction = Manufacturability.Unit(pull);
        var samples = faces.SelectMany((face, index) => PointsOn(face).Select(sample => (Face: index, sample.Point, sample.Normal))).ToList();
        var points = new List<double>();
        var vectors = new List<double>();
        foreach (var (_, point, normal) in samples)
        {
            points.AddRange([point.X - (normal.X * RayStart), point.Y - (normal.Y * RayStart), point.Z - (normal.Z * RayStart)]);
            vectors.AddRange([-normal.X, -normal.Y, -normal.Z]);
            var facing = Math.Sign(Dot(normal, direction));
            points.AddRange([point.X + (normal.X * RayStart), point.Y + (normal.Y * RayStart), point.Z + (normal.Z * RayStart)]);
            vectors.AddRange([direction.X * facing, direction.Y * facing, direction.Z * facing]);
        }

        var nearest = new double?[samples.Count * 2];
        if (samples.Count > 0 && document.Extension.RayIntersections(bodies, points.ToArray(), vectors.ToArray(), RayEntryAndExit, 0, 0, true) > 0)
        {
            var hits = document.GetRayIntersectionsPoints() as double[] ?? [];
            for (var at = 0; at + RayHitValues <= hits.Length; at += RayHitValues)
            {
                var ray = (int)hits[at + 1];
                var start = new Point3(points[ray * 3], points[(ray * 3) + 1], points[(ray * 3) + 2]);
                var distance = Math.Sqrt(Math.Pow(hits[at + 3] - start.X, 2) + Math.Pow(hits[at + 4] - start.Y, 2) + Math.Pow(hits[at + 5] - start.Z, 2));
                if (distance > RayStart && (nearest[ray] is null || distance < nearest[ray]))
                {
                    nearest[ray] = distance;
                }
            }
        }

        return [.. faces.Select((face, index) =>
        {
            var mine = samples.Select((sample, at) => (sample, at)).Where(s => s.sample.Face == index).ToList();
            var first = mine.FirstOrDefault();
            var thickness = mine.Select(s => nearest[s.at * 2]).Where(d => d is not null).Select(d => (d!.Value + RayStart) * Mm).DefaultIfEmpty(double.NaN).Min();
            var blocked = mine.Any(s => Math.Abs(Dot(s.sample.Normal, direction)) > 1e-3 && nearest[(s.at * 2) + 1] is not null);
            var point = mine.Count == 0 ? default : new Point3(first.sample.Point.X * Mm, first.sample.Point.Y * Mm, first.sample.Point.Z * Mm);
            return new FaceProbe(HandleOf(id, document, "face", face), SurfaceOf(face), point, mine.Count == 0 ? default : first.sample.Normal, double.IsNaN(thickness) ? null : thickness, blocked);
        })];
    });

    public SheetBends? ReadSheetBends(string id) => Call(() =>
    {
        var document = Find(id);
        var sheet = AllFeatures(document).FirstOrDefault(f => f.GetTypeName2() == "SheetMetal")?.GetDefinition() as ISheetMetalFeatureData;
        if (sheet is null)
        {
            return null;
        }

        var bends = AllFeatures(document)
            .Select(f => (Feature: f, Bend: f.GetDefinition() as IOneBendFeatureData))
            .Where(b => b.Bend is not null)
            .Select(b => (b.Feature.Name, b.Bend!.BendRadius * Mm))
            .DistinctBy(b => b.Name)
            .ToList();
        return new SheetBends(sheet.Thickness * Mm, bends);
    });

    // Points really on the face with the outward normal there. A render triangle's centre lies slightly inside a curved
    // face, so it is moved onto the surface; its render normals already point out of the material.
    private static List<(Point3 Point, Point3 Normal)> PointsOn(IFace2 face)
    {
        if (face.GetTessTriangles(true) is not float[] { Length: >= 9 } triangles || face.GetTessNorms() is not float[] normals || normals.Length < triangles.Length)
        {
            return [];
        }

        return [.. Enumerable.Range(0, triangles.Length / 9)
            .OrderByDescending(t => Area(Corner(triangles, t, 0), Corner(triangles, t, 1), Corner(triangles, t, 2)))
            .Take(SamplesPerFace)
            .Select(t =>
            {
                var (p, q, r) = (Corner(triangles, t, 0), Corner(triangles, t, 1), Corner(triangles, t, 2));
                var centre = new Point3((p.X + q.X + r.X) / 3, (p.Y + q.Y + r.Y) / 3, (p.Z + q.Z + r.Z) / 3);
                var on = face.GetClosestPointOn(centre.X, centre.Y, centre.Z) is double[] { Length: >= 3 } c ? new Point3(c[0], c[1], c[2]) : centre;
                var (a, b, d) = (Corner(normals, t, 0), Corner(normals, t, 1), Corner(normals, t, 2));
                return (on, Manufacturability.Unit(new Point3(a.X + b.X + d.X, a.Y + b.Y + d.Y, a.Z + b.Z + d.Z)));
            })];
    }

    private static double Area(Point3 a, Point3 b, Point3 c)
    {
        var (u, v) = (new Point3(b.X - a.X, b.Y - a.Y, b.Z - a.Z), new Point3(c.X - a.X, c.Y - a.Y, c.Z - a.Z));
        return Math.Pow((u.Y * v.Z) - (u.Z * v.Y), 2) + Math.Pow((u.Z * v.X) - (u.X * v.Z), 2) + Math.Pow((u.X * v.Y) - (u.Y * v.X), 2);
    }

    private static double Dot(Point3 a, Point3 b) => (a.X * b.X) + (a.Y * b.Y) + (a.Z * b.Z);

    private static Point3 Corner(float[] values, int triangle, int corner) =>
        new(values[(triangle * 9) + (corner * 3)], values[(triangle * 9) + (corner * 3) + 1], values[(triangle * 9) + (corner * 3) + 2]);
}
