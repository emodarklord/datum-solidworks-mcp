using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private static readonly Dictionary<int, string> EndConditions = new()
    {
        [(int)swEndConditions_e.swEndCondBlind] = "blind",
        [(int)swEndConditions_e.swEndCondThroughAll] = "through_all",
        [(int)swEndConditions_e.swEndCondMidPlane] = "mid_plane",
    };

    // ISketchArc.GetRotationDir gives 1 for counterclockwise and -1 for clockwise; there is no enum for it.
    private const int CounterClockwise = 1;

    // Features in tree order, sketches before the feature that uses them, as sketch_begin, sketch_add, sketch_end and
    // the feature tool. Anything those tools can't make again is named in Skipped instead. Faces and edges are described
    // as they were when the feature used them, since later features change their areas and lengths.
    public Lesson ReadLesson(string id, IReadOnlyList<string> features) => Call(() =>
    {
        var document = Find(id);
        var wanted = features.ToHashSet(StringComparer.Ordinal);
        var steps = new List<TaughtStep>();
        var skipped = new List<string>();
        var seen = new Seen([], []);
        foreach (var feature in AllFeatures(document).DistinctBy(f => f.Name))
        {
            var handle = HandleOf(id, document, "feat", feature);
            if (!wanted.Contains(handle))
            {
                continue;
            }

            var type = TypeOf(feature);
            var taught = type switch
            {
                "ProfileFeature" => SketchSteps(id, document, feature, handle, seen),
                "Extrusion" or "Boss" or "Cut" => ExtrudeStep(id, document, feature, handle),
                "Revolution" or "RevCut" => RevolveStep(id, document, feature, handle),
                "Fillet" => FilletStep(id, document, feature, handle, seen),
                _ => null,
            };
            if (taught is null)
            {
                skipped.Add($"{feature.Name} ({type})");
            }
            else
            {
                steps.AddRange(taught);
            }
        }

        return new Lesson(steps, skipped, seen.Faces, seen.Edges);
    });

    private List<TaughtStep>? SketchSteps(string id, IModelDoc2 document, IFeature feature, string handle, Seen seen)
    {
        var sketch = (ISketch)feature.GetSpecificFeature2();
        var on = PlaneOf(sketch) ?? FaceUnder(id, document, feature, sketch, seen);
        var entities = SegmentsOf(sketch).Select(EntityOf).ToList();
        if (on is null || entities.Contains(null))
        {
            return null;
        }

        return
        [
            new TaughtStep("sketch_begin", new Dictionary<string, object?> { ["on"] = on }),
            new TaughtStep("sketch_add", new Dictionary<string, object?> { ["entities"] = entities }),
            new TaughtStep("sketch_end", new Dictionary<string, object?>(), handle),
        ];
    }

    private sealed record Seen(List<FaceInfo> Faces, List<EdgeInfo> Edges);

    private static string? PlaneOf(ISketch sketch)
    {
        var type = 0;
        var name = sketch.GetReferenceEntity(ref type) is IRefPlane plane ? ((IFeature)plane).Name.Split(' ')[0] : null;
        return name is "Front" or "Top" or "Right" ? name : null;
    }

    // The face a sketch sits on, as it was before the feature that used the sketch, read with the rollback bar there.
    private string? FaceUnder(string id, IModelDoc2 document, IFeature feature, ISketch sketch, Seen seen)
    {
        var user = (feature.GetChildren() as object[] ?? []).OfType<IFeature>().FirstOrDefault();
        if (user is not null)
        {
            document.FeatureManager.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToBeforeFeature, user.Name);
        }

        try
        {
            var type = 0;
            if (sketch.GetReferenceEntity(ref type) is not IFace2 face)
            {
                return null;
            }

            var described = DescribeFace(id, document, face);
            seen.Faces.Add(described);
            return described.Handle;
        }
        finally
        {
            if (user is not null)
            {
                document.FeatureManager.EditRollback((int)swMoveRollbackBarTo_e.swMoveRollbackBarToEnd, "");
            }
        }
    }

    // sketch_add's arc goes through three points, so the middle of the arc is worked out from its turning direction.
    private static Dictionary<string, object?>? EntityOf(ISketchSegment segment)
    {
        switch (segment)
        {
            case ISketchLine line:
                var (start, end) = (PointOf(line.GetStartPoint2()), PointOf(line.GetEndPoint2()));
                return new() { ["type"] = segment.ConstructionGeometry ? "centerline" : "line", ["x1"] = start.X, ["y1"] = start.Y, ["x2"] = end.X, ["y2"] = end.Y };
            case ISketchArc arc when !segment.ConstructionGeometry:
                var (from, to, centre, radius) = (PointOf(arc.GetStartPoint2()), PointOf(arc.GetEndPoint2()), PointOf(arc.GetCenterPoint2()), Math.Round(arc.GetRadius() * Mm, 6));
                if (from == to)
                {
                    return new() { ["type"] = "circle", ["cx"] = centre.X, ["cy"] = centre.Y, ["radius"] = radius };
                }

                var (a, b) = (Math.Atan2(from.Y - centre.Y, from.X - centre.X), Math.Atan2(to.Y - centre.Y, to.X - centre.X));
                var sweep = arc.GetRotationDir() == CounterClockwise ? (b - a + (4 * Math.PI)) % (2 * Math.PI) : -((a - b + (4 * Math.PI)) % (2 * Math.PI));
                var middle = a + (sweep / 2);
                return new()
                {
                    ["type"] = "arc",
                    ["x1"] = from.X,
                    ["y1"] = from.Y,
                    ["x2"] = to.X,
                    ["y2"] = to.Y,
                    ["x3"] = Math.Round(centre.X + (radius * Math.Cos(middle)), 6),
                    ["y3"] = Math.Round(centre.Y + (radius * Math.Sin(middle)), 6),
                };
            default:
                return null;
        }
    }

    private List<TaughtStep>? ExtrudeStep(string id, IModelDoc2 document, IFeature feature, string handle)
    {
        var definition = (IExtrudeFeatureData2)feature.GetDefinition();
        if (SketchOf(id, document, feature) is not { } sketch || !EndConditions.TryGetValue(definition.GetEndCondition(true), out var end) || definition.BothDirections)
        {
            return null;
        }

        var args = new Dictionary<string, object?> { ["sketch"] = sketch, ["cut"] = TypeOf(feature) == "Cut", ["end"] = end, ["reverse"] = definition.ReverseDirection };
        if (end != "through_all")
        {
            args["depth"] = Math.Round(definition.GetDepth(true) * Mm, 6);
        }

        return [new TaughtStep("feature_extrude", args, handle)];
    }

    private List<TaughtStep>? RevolveStep(string id, IModelDoc2 document, IFeature feature, string handle)
    {
        var definition = (IRevolveFeatureData2)feature.GetDefinition();
        return SketchOf(id, document, feature) is { } sketch
            ? [new TaughtStep("feature_revolve", new Dictionary<string, object?> { ["sketch"] = sketch, ["angle"] = Math.Round(definition.GetRevolutionAngle(true) * 180 / Math.PI, 6), ["cut"] = feature.GetTypeName2() == "RevCut" }, handle)]
            : null;
    }

    // The fillet's edges can only be read while its selections are open, which rolls the model back to it.
    private List<TaughtStep>? FilletStep(string id, IModelDoc2 document, IFeature feature, string handle, Seen seen)
    {
        if (feature.GetDefinition() is not ISimpleFilletFeatureData2 definition || !definition.AccessSelections(document, null))
        {
            return null;
        }

        try
        {
            var items = definition.Edges as object[] ?? [];
            if (items.Length == 0 || !items.All(item => item is IEdge))
            {
                return null;
            }

            var edges = items.OfType<IEdge>().Select(edge => DescribeEdge(id, document, edge)).ToList();
            seen.Edges.AddRange(edges);
            seen.Faces.AddRange(items.OfType<IEdge>().SelectMany(edge => (edge.GetTwoAdjacentFaces2() as object[] ?? []).OfType<IFace2>()).Select(face => DescribeFace(id, document, face)));
            return [new TaughtStep("feature_fillet", new Dictionary<string, object?> { ["edges"] = edges.Select(edge => edge.Handle).ToArray(), ["radius"] = Math.Round(definition.DefaultRadius * Mm, 6) }, handle)];
        }
        finally
        {
            definition.ReleaseSelectionAccess();
        }
    }

    // Extrudes made through the API can report ICE here; the older call still gives Extrusion or Cut.
    private static string TypeOf(IFeature feature) => feature.GetTypeName2() is "ICE" ? feature.GetTypeName() : feature.GetTypeName2();

    private string? SketchOf(string id, IModelDoc2 document, IFeature feature)
    {
        for (var sub = feature.GetFirstSubFeature() as IFeature; sub is not null; sub = sub.GetNextSubFeature() as IFeature)
        {
            if (sub.GetTypeName2() == "ProfileFeature")
            {
                return HandleOf(id, document, "feat", sub);
            }
        }

        return null;
    }
}
