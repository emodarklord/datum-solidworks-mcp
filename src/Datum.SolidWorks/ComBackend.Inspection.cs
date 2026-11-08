using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const double Mm = 1000;

    private readonly HandleRegistry _handles = new();

    public IReadOnlyList<FeatureInfo> ListFeatures(string id) => Call(() =>
    {
        var document = Find(id);
        var tree = new List<(FeatureInfo Feature, List<FeatureInfo> Children)>();
        for (var feature = document.FirstFeature() as IFeature; feature is not null; feature = feature.GetNextFeature() as IFeature)
        {
            if (IsTreeNoise(feature))
            {
                continue;
            }

            var children = new List<FeatureInfo>();
            for (var sub = feature.GetFirstSubFeature() as IFeature; sub is not null; sub = sub.GetNextSubFeature() as IFeature)
            {
                children.Add(DescribeFeature(id, document, sub, 1));
            }

            tree.Add((DescribeFeature(id, document, feature, 0), children));
        }

        // The top-level walk also returns sketches a later feature absorbed; show them only under that feature.
        var absorbed = tree.SelectMany(t => t.Children).Select(c => c.Handle).ToHashSet();
        return tree.Where(t => !absorbed.Contains(t.Feature.Handle)).SelectMany(t => t.Children.Prepend(t.Feature)).ToList();
    });

    public ModelInfo ReadModel(string id) => Call(() =>
    {
        var document = Find(id);
        var info = DescribeActive(document);
        var mass = document.Extension.CreateMassProperty() as IMassProperty;
        var center = mass?.CenterOfMass as double[] ?? [0, 0, 0];
        Box? box = null;
        string? material = null;
        var bodies = 0;
        if (document is IPartDoc part)
        {
            // GetPartBox also counts visible sketches, so a stray sketch would inflate the size; use the solid bodies only.
            var solids = SolidBodies(part);
            box = solids.Select(body => body.GetBodyBox() as double[]).OfType<double[]>().Where(b => b.Length == 6)
                .Aggregate((Box?)null, (union, b) => new Box(
                    new(Math.Min(union?.Min.X ?? double.MaxValue, b[0] * Mm), Math.Min(union?.Min.Y ?? double.MaxValue, b[1] * Mm), Math.Min(union?.Min.Z ?? double.MaxValue, b[2] * Mm)),
                    new(Math.Max(union?.Max.X ?? double.MinValue, b[3] * Mm), Math.Max(union?.Max.Y ?? double.MinValue, b[4] * Mm), Math.Max(union?.Max.Z ?? double.MinValue, b[5] * Mm))));
            var configuration = (document.GetActiveConfiguration() as IConfiguration)?.Name ?? "";
            material = part.GetMaterialPropertyName2(configuration, out _) is { Length: > 0 } name ? name : null;
            bodies = solids.Count;
        }

        if (document is IAssemblyDoc)
        {
            var parts = ComponentsOf(document, id).Where(c => !c.IsSuppressed()).ToList();
            box = parts.Select(component => component.GetBox(false, false) as double[]).OfType<double[]>().Where(b => b.Length == 6)
                .Aggregate((Box?)null, (union, b) => new Box(
                    new(Math.Min(union?.Min.X ?? double.MaxValue, b[0] * Mm), Math.Min(union?.Min.Y ?? double.MaxValue, b[1] * Mm), Math.Min(union?.Min.Z ?? double.MaxValue, b[2] * Mm)),
                    new(Math.Max(union?.Max.X ?? double.MinValue, b[3] * Mm), Math.Max(union?.Max.Y ?? double.MinValue, b[4] * Mm), Math.Max(union?.Max.Z ?? double.MinValue, b[5] * Mm))));
            bodies = parts.Sum(component => BodiesOf(component).Count);
        }

        return new ModelInfo(
            info.Title,
            info.Type,
            material,
            (mass?.Mass ?? 0) * 1000,
            (mass?.Volume ?? 0) * Mm * Mm * Mm,
            (mass?.SurfaceArea ?? 0) * Mm * Mm,
            new(center[0] * Mm, center[1] * Mm, center[2] * Mm),
            box,
            bodies);
    });

    public IReadOnlyList<FaceInfo> ListFaces(string id, string? feature = null) => Call(() =>
    {
        var document = Find(id);
        return FacesOf(document).Where(face => MadeBy(face, feature)).Select(face => DescribeFace(id, document, face)).ToList();
    });

    private static bool MadeBy(IFace2 face, string? feature) =>
        feature is null || string.Equals((face.GetFeature() as IFeature)?.Name, feature, StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<EdgeInfo> ListEdges(string id) => Call(() =>
    {
        var document = Find(id);
        return EdgesOf(document).Select(edge => DescribeEdge(id, document, edge)).ToList();
    });

    public IReadOnlyList<SelectionItem> ReadSelection(string id) => Call(() =>
    {
        var document = Find(id);
        var selection = (ISelectionMgr)document.SelectionManager;
        var items = new List<SelectionItem>();
        for (var i = 1; i <= selection.GetSelectedObjectCount2(-1); i++)
        {
            var type = (swSelectType_e)selection.GetSelectedObjectType3(i, -1);
            var selected = selection.GetSelectedObject6(i, -1);
            items.Add(type switch
            {
                swSelectType_e.swSelFACES => new SelectionItem(HandleOf(id, document, "face", selected), "face"),
                swSelectType_e.swSelEDGES => new SelectionItem(HandleOf(id, document, "edge", selected), "edge"),
                _ when selected is IFeature => new SelectionItem(HandleOf(id, document, "feat", selected), "feature"),
                swSelectType_e.swSelVERTICES => new SelectionItem(null, "vertex"),
                _ => new SelectionItem(null, type.ToString().Replace("swSel", "", StringComparison.Ordinal).ToUpperInvariant()),
            });
        }

        return items;
    });

    public void SelectEntities(string id, IReadOnlyList<string> entityHandles) => Call(() =>
    {
        var document = Find(id);
        Select(document, [.. entityHandles.Select(handle => Resolve(id, document, handle))], entityHandles);
        return true;
    });

    private object Resolve(string id, IModelDoc2 document, string handle)
    {
        var entry = _handles.Find(handle);
        if (entry.DocumentId != id)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{handle} belongs to document {entry.DocumentId}, not {id}");
        }

        return entry.Live ?? document.Extension.GetObjectByPersistReference3(entry.Reference, out _)
            ?? throw new DatumException(ErrorCode.NotFound, $"{handle} no longer exists in {id}", "The model changed; call model_faces or model_tree again");
    }

    private string HandleOf(string id, IModelDoc2 document, string kind, object entity) =>
        document.Extension.GetPersistReference3(entity) is byte[] { Length: > 0 } reference
            ? _handles.HandleFor(id, kind, reference)
            : _handles.HandleForLive(id, kind, entity);

    private FeatureInfo DescribeFeature(string id, IModelDoc2 document, IFeature feature, int depth)
    {
        var suppressed = feature.IsSuppressed2((int)swInConfigurationOpts_e.swThisConfiguration, null) is bool[] { Length: > 0 } states && states[0];
        var error = feature.GetErrorCode2(out var isWarning);
        return new FeatureInfo(
            HandleOf(id, document, "feat", feature),
            feature.Name,
            feature.GetTypeName2(),
            depth,
            suppressed,
            error == 0 ? null : $"{(isWarning ? "warning" : "error")} {(swFeatureError_e)error}");
    }

    // The fixed folders at the top of every FeatureManager tree say nothing about the model.
    private static bool IsTreeNoise(IFeature feature)
    {
        var type = feature.GetTypeName2();
        return type.EndsWith("Folder", StringComparison.Ordinal) || type is "DetailCabinet" or "Attribute" or "MateGroup";
    }

    private static SurfaceKind SurfaceOf(IFace2 face) => (ISurface)face.GetSurface() switch
    {
        var s when s.IsPlane() => SurfaceKind.Plane,
        var s when s.IsCylinder() => SurfaceKind.Cylinder,
        var s when s.IsCone() => SurfaceKind.Cone,
        var s when s.IsSphere() => SurfaceKind.Sphere,
        var s when s.IsTorus() => SurfaceKind.Torus,
        _ => SurfaceKind.Other,
    };

    private FaceInfo DescribeFace(string id, IModelDoc2 document, IFace2 face)
    {
        var surface = (ISurface)face.GetSurface();
        var kind = SurfaceOf(face);
        var box = face.GetBox() as double[] ?? [0, 0, 0, 0, 0, 0];
        Point3? normal = kind == SurfaceKind.Plane && face.Normal is double[] { Length: 3 } n ? new Point3(n[0], n[1], n[2]) : null;
        double? radius = kind == SurfaceKind.Cylinder && surface.CylinderParams is double[] { Length: >= 7 } c ? c[6] * Mm : null;
        return new FaceInfo(
            HandleOf(id, document, "face", face),
            kind,
            face.GetArea() * Mm * Mm,
            normal,
            new((box[0] + box[3]) / 2 * Mm, (box[1] + box[4]) / 2 * Mm, (box[2] + box[5]) / 2 * Mm),
            radius,
            (face.GetFeature() as IFeature)?.Name);
    }

    private EdgeInfo DescribeEdge(string id, IModelDoc2 document, IEdge edge)
    {
        var curve = (ICurve)edge.GetCurve();
        var kind = curve.IsLine() ? CurveKind.Line : curve.IsCircle() ? CurveKind.Circle : CurveKind.Other;
        var parameters = edge.GetCurveParams3();
        double? radius = kind == CurveKind.Circle && curve.CircleParams is double[] { Length: >= 7 } c ? c[6] * Mm : null;
        Point3? direction = kind == CurveKind.Line && curve.LineParams is double[] { Length: >= 6 } l ? new Point3(l[3], l[4], l[5]) : null;
        var faces = (edge.GetTwoAdjacentFaces2() as object[] ?? []).OfType<IFace2>().Select(f => HandleOf(id, document, "face", f)).ToList();
        return new EdgeInfo(
            HandleOf(id, document, "edge", edge),
            kind,
            curve.GetLength3(parameters.UMinValue, parameters.UMaxValue) * Mm,
            radius,
            faces,
            direction);
    }

    private static List<IBody2> SolidBodies(IPartDoc part) =>
        [.. (part.GetBodies2((int)swBodyType_e.swSolidBody, true) as object[] ?? []).OfType<IBody2>()];

    private static IEnumerable<IFace2> FacesOf(IModelDoc2 document) =>
        document is IPartDoc part ? SolidBodies(part).SelectMany(b => (b.GetFaces() as object[] ?? []).OfType<IFace2>()) : [];

    private static IEnumerable<IEdge> EdgesOf(IModelDoc2 document) =>
        document is IPartDoc part ? SolidBodies(part).SelectMany(b => (b.GetEdges() as object[] ?? []).OfType<IEdge>()) : [];
}
