using System.ComponentModel;
using System.Globalization;
using System.Text;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class InspectionTools
{
    private static readonly TimeSpan GeometryTimeout = TimeSpan.FromMinutes(5);

    [McpServerTool(Name = "model_tree", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List a document's features in tree order with handles (feat#N); sketches are indented under the feature that uses them.")]
    public static Task<CallToolResult> Tree(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var features = await client.RunAsync(b => b.ListFeatures(target.Id), GeometryTimeout, cancellationToken).ConfigureAwait(false);
                var report = new StringBuilder($"{target.Title}: {features.Count} features\n");
                foreach (var feature in features)
                {
                    report.Append(new string(' ', feature.Depth * 2))
                        .Append(CultureInfo.InvariantCulture, $"{feature.Handle} {feature.Name} [{feature.TypeName}]")
                        .Append(feature.Suppressed ? " suppressed" : "")
                        .Append(feature.Error is null ? "" : $" ERROR: {feature.Error}")
                        .Append('\n');
                }

                return report.ToString();
            },
            cancellationToken));

    [McpServerTool(Name = "model_info", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Mass, volume, surface area, centre of mass, bounding box and material of a part or assembly, in mm and grams.")]
    public static Task<CallToolResult> Info(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                if (target.Type == DocumentType.Drawing)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} is a drawing", "Use model_info on the part or assembly it shows");
                }

                var model = await client.RunAsync(b => b.ReadModel(target.Id), GeometryTimeout, cancellationToken).ConfigureAwait(false);
                var report = new StringBuilder()
                    .Append(CultureInfo.InvariantCulture, $"{model.Title} ({DocumentTools.Label(model.Type)}), {model.Bodies} {(model.Bodies == 1 ? "body" : "bodies")}\n")
                    .Append(CultureInfo.InvariantCulture, $"Mass {N(model.MassGrams)} g, volume {N(model.VolumeMm3)} mm³, surface {N(model.AreaMm2)} mm²\n")
                    .Append(CultureInfo.InvariantCulture, $"Centre of mass {model.CenterOfMass} mm\n");
                if (model.Box is { } box)
                {
                    var size = box.Size;
                    report.Append(CultureInfo.InvariantCulture, $"Bounding box {N(size.X)} x {N(size.Y)} x {N(size.Z)} mm, from {box.Min} to {box.Max}\n");
                }

                return report.Append(model.Material is null ? "No material set\n" : $"Material {model.Material}\n").ToString();
            },
            cancellationToken));

    [McpServerTool(Name = "model_faces", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List a part's faces (largest first) or edges (longest first) with handles that other tools accept, or those of one assembly component, in assembly coordinates. Filter to keep the reply short.")]
    public static Task<CallToolResult> Faces(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        [Description("face or edge")] string kind = "face",
        [Description("Faces only: plane, cylinder, cone, sphere, torus or other")] string? surface = null,
        [Description("Faces only: outward normal, e.g. +z, -x or 0,0.7,0.7")] string? normal = null,
        [Description("Faces only: name of the feature that made the face")] string? feature = null,
        [Description("Edges only: line, circle or other")] string? curve = null,
        [Description("Edges only: a face handle; lists that face's edges")] string? face = null,
        [Description("Edges only: straight edges parallel to this direction, e.g. z for vertical edges, or 1,1,0")] string? direction = null,
        [Description("Maximum lines to return")] int limit = 50,
        [Description("Lines to skip, for paging")] int offset = 0,
        [Description("In an assembly: the component to list, comp#N")] string? component = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                if (target.Type == DocumentType.Drawing || (target.Type == DocumentType.Assembly && component is null))
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} is not a part", "In an assembly, name a component; drawings have no faces to list");
                }

                return kind.Trim().ToUpperInvariant() switch
                {
                    "FACE" => FacePage(EntityQuery.Faces(
                        await client.RunAsync(b => component is null ? b.ListFaces(target.Id, feature) : b.ListComponentFaces(target.Id, component, feature), GeometryTimeout, cancellationToken).ConfigureAwait(false),
                        surface, normal, feature, offset, limit)),
                    "EDGE" => EdgePage(EntityQuery.Edges(
                        await client.RunAsync(b => component is null ? b.ListEdges(target.Id) : b.ListComponentEdges(target.Id, component), GeometryTimeout, cancellationToken).ConfigureAwait(false),
                        curve, face, direction, offset, limit)),
                    _ => throw new DatumException(ErrorCode.InvalidArgument, $"unknown kind '{kind}'", "Use face or edge"),
                };
            },
            cancellationToken));

    [McpServerTool(Name = "model_select", Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Select faces, edges or features by handle in SOLIDWORKS, replacing the current selection, so the user can see them. Doesn't change the model.")]
    public static Task<CallToolResult> Select(
        SolidWorksConnection solidWorks,
        [Description("Handles such as face#12, edge#40 or feat#3")] string[] handles,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                await client.RunAsync(b => { b.SelectEntities(target.Id, handles); return true; }, cancellationToken: cancellationToken).ConfigureAwait(false);
                return $"Selected {string.Join(", ", handles)} in {target.Title}\n";
            },
            cancellationToken));

    [McpServerTool(Name = "selection_get", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("What is selected in SOLIDWORKS right now, as handles. Use it when the user says 'this' or 'these'.")]
    public static Task<CallToolResult> Selection(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var selection = await client.RunAsync(b => b.ReadSelection(target.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
                return selection.Count == 0
                    ? $"Nothing is selected in {target.Title}\n"
                    : string.Concat(selection.Select(s => (s.Handle ?? $"{s.Kind} (no handle)") + "\n"));
            },
            cancellationToken));

    private static string FacePage(EntityPage<FaceInfo> page) => Footer(page, page.Items.Select(f => $"{f.Handle} {FaceKey(f)}"));

    internal static string EdgeKey(EdgeInfo e) => $"{Kind(e.Curve)} {N(e.LengthMm)} mm" + (e.RadiusMm is { } r ? $" radius {N(r)} mm" : "");

    // Faces and edges by their geometry, for Intent to find them again in another session. A handle it can't describe is
    // left out, and replay then says so.
    internal static async Task<Dictionary<string, string>> DescribeAsync(SolidWorksConnection solidWorks, IReadOnlyList<string> handles, CancellationToken cancellationToken)
    {
        try
        {
            return await solidWorks.UseAsync(
                async client =>
                {
                    var active = (await client.RunAsync(b => b.ListDocuments(), cancellationToken: cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.Active && d.Type == DocumentType.Part);
                    if (active is null)
                    {
                        return new Dictionary<string, string>();
                    }

                    var faces = (await client.RunAsync(b => b.ListFaces(active.Id), cancellationToken: cancellationToken).ConfigureAwait(false)).ToDictionary(f => f.Handle, FaceKey, StringComparer.OrdinalIgnoreCase);
                    var edges = await client.RunAsync(b => b.ListEdges(active.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
                    var described = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var handle in handles)
                    {
                        if (faces.TryGetValue(handle, out var face))
                        {
                            described[handle] = $"face:{face}";
                        }
                        else if (edges.FirstOrDefault(e => string.Equals(e.Handle, handle, StringComparison.OrdinalIgnoreCase)) is { Faces.Count: 2 } edge)
                        {
                            described[handle] = $"edge:{EdgeKey(edge)}|{string.Join("|", edge.Faces.Select(f => faces.GetValueOrDefault(f, "")))}";
                        }
                    }

                    return described;
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (DatumException)
        {
            return [];
        }
    }

    // Everything about a face but its handle, which is what Intent matches it by on replay.
    internal static string FaceKey(FaceInfo f) =>
        $"{Kind(f.Surface)} {N(f.AreaMm2)} mm²"
        + (f.Normal is { } n ? $" normal {EntityQuery.DescribeDirection(n)}" : "")
        + (f.RadiusMm is { } r ? $" radius {N(r)} mm" : "")
        + $" centre {f.Center}"
        + (f.Feature is null ? "" : $" ({f.Feature})");

    private static string EdgePage(EntityPage<EdgeInfo> page) =>
        Footer(page, page.Items.Select(e =>
            $"{e.Handle} {EdgeKey(e)}"
            + (e.Faces.Count > 0 ? $" faces {string.Join(",", e.Faces)}" : "")));

    private static string Footer<T>(EntityPage<T> page, IEnumerable<string> lines)
    {
        var report = new StringBuilder();
        foreach (var line in lines)
        {
            report.Append(line).Append('\n');
        }

        if (page.Total == 0)
        {
            return "Nothing matches\n";
        }

        if (page.Items.Count == 0)
        {
            return string.Create(CultureInfo.InvariantCulture, $"No more: {page.Total} match in total\n");
        }

        var last = page.Offset + page.Items.Count;
        return page.Items.Count < page.Total
            ? report.Append(CultureInfo.InvariantCulture, $"showing {page.Offset + 1}-{last} of {page.Total}")
                .Append(last < page.Total ? $"; pass offset {last} for more\n" : "\n").ToString()
            : report.ToString();
    }

    private static string Kind<T>(T kind)
        where T : struct, Enum => kind.ToString() switch
        {
            "Plane" => "plane",
            "Cylinder" => "cylinder",
            "Cone" => "cone",
            "Sphere" => "sphere",
            "Torus" => "torus",
            "Line" => "line",
            "Circle" => "circle",
            _ => "other",
        };

    private static string N(double value) => Numbers.Format(value);
}
