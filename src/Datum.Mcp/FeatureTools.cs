using System.ComponentModel;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class FeatureTools
{
    private const string Toolset = "features";

    private static readonly TimeSpan FeatureTimeout = TimeSpan.FromMinutes(2);

    [McpServerTool(Name = "feature_extrude", Destructive = false, OpenWorld = false)]
    [Description("Extrude a sketch (feat# from sketch_end) into a boss, or cut it from the part. Depth in mm. reverse flips the side: a boss normally grows along the sketch normal and a cut goes against it. "
        + "More features (revolve, fillet, chamfer, hole, pattern, mirror, shell) are in toolset 'features': "
        + "use feature_hole for round holes and feature_pattern to repeat a feature, rather than sketching and cutting each one.")]
    public static Task<CallToolResult> Extrude(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Sketch handle, feat#N")] string sketch,
        [Description("Depth in mm; not needed for through_all")] double? depth = null,
        [Description("Cut material away instead of adding it")] bool cut = false,
        [Description("blind, through_all or mid_plane")] string end = "blind",
        [Description("Go the other way from the sketch plane")] bool reverse = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new ExtrudeSpec(sketch, depth, cut, end, reverse), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_revolve", Destructive = false, OpenWorld = false)]
    [Description("Revolve a sketch about its one centerline, adding or cutting material. Angle in degrees.")]
    public static Task<CallToolResult> Revolve(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Sketch handle, feat#N; it must contain exactly one centerline")] string sketch,
        [Description("Degrees, up to 360")] double angle = 360,
        [Description("Cut material away instead of adding it")] bool cut = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new RevolveSpec(sketch, angle, cut), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_sweep", Destructive = false, OpenWorld = false)]
    [Description("Sweep a section along a path sketch, which may be 3D. Give a closed profile sketch that starts on the path and is square to it, or just a diameter for a round bar, and a wall as well for a tube. "
        + "The wall is measured inward, so the diameter stays the outside size.")]
    public static Task<CallToolResult> Sweep(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Path sketch handle, feat#N")] string path,
        [Description("Closed profile sketch handle, feat#N; leave out when giving a diameter")] string? profile = null,
        [Description("Outside diameter of a round section in mm")] double? diameter = null,
        [Description("Tube wall in mm, with a diameter")] double? wall = null,
        [Description("Join to what it touches; false keeps it a separate body")] bool merge = true,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new SweepSpec(path, profile, diameter, wall, merge), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_rib", Destructive = false, OpenWorld = false)]
    [Description("Add a stiffening rib from an open line sketched on a plane through the part. The line can stop short of the walls: the rib grows to meet them. Thickness in mm.")]
    public static Task<CallToolResult> Rib(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Sketch handle, feat#N")] string sketch,
        [Description("Rib thickness")] double thickness,
        [Description("Thicken both sides of the line; false uses one side")] bool bothSides = true,
        [Description("Grow towards the other side of the line")] bool flip = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new RibSpec(sketch, thickness, bothSides, flip), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_loft", Destructive = false, OpenWorld = false)]
    [Description("Blend between two or more closed profile sketches on separate planes, in order, with optional guide sketches that steer the shape between them.")]
    public static Task<CallToolResult> Loft(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Profile sketch handles in order, feat#N")] string[] profiles,
        [Description("Guide sketch handles, each touching every profile")] string[]? guides = null,
        [Description("Join to what it touches; false keeps it a separate body")] bool merge = true,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new LoftSpec(profiles, guides ?? [], merge), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_fillet", Destructive = false, OpenWorld = false)]
    [Description("Round edges (edge# handles from model_faces kind=edge) with a constant radius in mm.")]
    public static Task<CallToolResult> Fillet(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Edge handles, or [\"selected\"] for the edges selected in SOLIDWORKS")] string[] edges,
        [Description("Radius in mm")] double radius,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new FilletSpec(edges, radius), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_chamfer", Destructive = false, OpenWorld = false)]
    [Description("Bevel edges (edge# handles). Equal distances at 45 degrees, or distance and angle.")]
    public static Task<CallToolResult> Chamfer(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Edge handles, or [\"selected\"] for the edges selected in SOLIDWORKS")] string[] edges,
        [Description("Distance in mm")] double distance,
        [Description("Angle in degrees")] double angle = 45,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new ChamferSpec(edges, distance, angle), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_hole", Destructive = false, OpenWorld = false)]
    [Description("Drill a plain round hole into a flat face at a point given in model mm (as model_faces reports them). Omit depth to go through all.")]
    public static Task<CallToolResult> Hole(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Flat face handle, face#N, or \"selected\"")] string face,
        [Description("Hole centre on that face as \"x,y,z\" in model mm")] string at,
        [Description("Diameter in mm")] double diameter,
        [Description("Depth in mm; omit for through all")] double? depth = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new HoleSpec(face, at, diameter, depth), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_pattern", Destructive = false, OpenWorld = false)]
    [Description("Repeat features in a line along a straight edge, or around a circular edge or cylindrical face. Count includes the original.")]
    public static Task<CallToolResult> Pattern(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("linear or circular")] string kind,
        [Description("Feature handles to repeat, feat#N")] string[] features,
        [Description("Linear: a straight edge#. Circular: a circular edge# or cylindrical face# to rotate about")] string along,
        [Description("Total copies including the original")] int count,
        [Description("Linear: mm between copies. Circular: degrees between copies; omit to space them evenly around 360")] double? spacing = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new PatternSpec(kind, features, along, count, spacing), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "plane_create", Destructive = false, OpenWorld = false)]
    [Description("Make a reference plane: parallel to a plane or flat face at an offset in mm (negative goes the other way), "
        + "or at an angle to it through a straight edge. Use the new feat# with sketch_begin.")]
    public static Task<CallToolResult> Plane(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Front, Top, Right, a plane feat# or a flat face#")] string from,
        [Description("Offset in mm, for a parallel plane")] double? offset = null,
        [Description("Angle in degrees, for an angled plane")] double? angle = null,
        [Description("A straight edge# the angled plane passes through")] string? through = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new PlaneSpec(from, offset, angle, through), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "axis_create", Destructive = false, OpenWorld = false)]
    [Description("Make a reference axis: along a cylindrical face (one face# handle), or where two planes meet.")]
    public static Task<CallToolResult> Axis(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("A cylindrical face#, or Front, Top, Right or a plane feat#")] string first,
        [Description("The second plane, when first is a plane")] string? second = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new AxisSpec(first, second), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_mirror", Destructive = false, OpenWorld = false)]
    [Description("Mirror features about Front, Top, Right, a plane (feat#) or a flat face (face#).")]
    public static Task<CallToolResult> Mirror(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Feature handles to mirror, feat#N")] string[] features,
        [Description("Front, Top, Right, a plane feat# or a flat face#")] string plane,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new MirrorSpec(features, plane), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "feature_shell", Destructive = false, OpenWorld = false)]
    [Description("Hollow the part to a wall thickness in mm, leaving the given faces open.")]
    public static Task<CallToolResult> Shell(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Wall thickness in mm")] double thickness,
        [Description("Faces to remove (left open), face#N, or [\"selected\"]")] string[] remove,
        [Description("Add the wall outside the current faces instead of inside")] bool outward = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        CreateAsync(solidWorks, workspace, doc, new ShellSpec(thickness, remove, outward), cancellationToken);

    internal static Task<CallToolResult> CreateAsync(
        SolidWorksConnection solidWorks, Workspace workspace, string? doc, FeatureSpec spec, CancellationToken cancellationToken) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var checkedSpec = spec is HoleSpec hole ? hole.Parsed() : spec;
                checkedSpec.Validate();
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                if (target.Type != DocumentType.Part)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} is not a part", "Feature tools work on parts");
                }

                DocumentTools.RequireEditable(workspace, target);
                var before = await client.RunAsync(b => b.ReadModel(target.Id), FeatureTimeout, cancellationToken).ConfigureAwait(false);
                var result = await client.RunAsync(b => b.CreateFeature(target.Id, checkedSpec), FeatureTimeout, cancellationToken).ConfigureAwait(false);
                var after = await client.RunAsync(b => b.ReadModel(target.Id), FeatureTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"before {result.Name}", new DeleteCreated(result.Created));
                return $"{result.Name} is {result.Handle} · {result.Change}{Box.Growth(before.Box, after.Box)}\n";
            },
            cancellationToken));
}
