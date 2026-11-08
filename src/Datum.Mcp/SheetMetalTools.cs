using System.ComponentModel;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class SheetMetalTools
{
    private const string Toolset = "sheetmetal";

    private static readonly TimeSpan SheetTimeout = TimeSpan.FromMinutes(2);

    [Toolset(Toolset)]
    [McpServerTool(Name = "sheet_base", Destructive = false, OpenWorld = false)]
    [Description("Start a sheet metal part from a sketch. A closed profile makes a flat blank. An open profile, such as an L, needs a depth and bends at each corner, which is the quickest way to a bent bracket. Sizes in mm.")]
    public static Task<CallToolResult> Base(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Sketch handle, feat#N")] string sketch,
        [Description("Sheet thickness")] double thickness,
        [Description("Inside bend radius")] double radius,
        [Description("How far an open profile extends; leave out for a closed one")] double? depth = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        FeatureTools.CreateAsync(solidWorks, workspace, doc, new SheetBaseSpec(sketch, thickness, radius, depth), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "sheet_bend", Destructive = false, OpenWorld = false)]
    [Description("Bend a flat along a straight line sketched across one of its faces.")]
    public static Task<CallToolResult> Bend(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Sketch handle holding the bend line, feat#N")] string sketch,
        [Description("Bend angle in degrees")] double angle,
        [Description("Inside bend radius in mm")] double radius,
        [Description("Bend the other way")] bool flip = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        FeatureTools.CreateAsync(solidWorks, workspace, doc, new SheetBendSpec(sketch, angle, radius, flip), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "sheet_hem", Destructive = false, OpenWorld = false)]
    [Description("Fold a straight outer edge back over itself.")]
    public static Task<CallToolResult> Hem(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Edge handles, edge#N, or [\"selected\"] for the edges selected in SOLIDWORKS")] string[] edges,
        [Description("Hem length in mm")] double length,
        [Description("Gap under the hem in mm")] double gap = 0.5,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        FeatureTools.CreateAsync(solidWorks, workspace, doc, new SheetHemSpec(edges, length, gap), cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "sheet_flatten", Destructive = false, OpenWorld = false)]
    [Description("Show a sheet metal part flat, or fold it back. model_info then gives the flat size. Fold it back before adding bends.")]
    public static Task<CallToolResult> Flatten(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("true for flat, false for formed")] bool flat = true,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                DocumentTools.RequireEditable(workspace, target);
                await client.RunAsync(b => b.SetFlat(target.Id, flat), SheetTimeout, cancellationToken).ConfigureAwait(false);
                var model = await client.RunAsync(b => b.ReadModel(target.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
                return $"{target.Title} is {(flat ? "flat" : "formed")}{(model.Box is { Size: var size } ? $", box {Numbers.Format(size.X)} x {Numbers.Format(size.Y)} x {Numbers.Format(size.Z)} mm" : "")}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "sheet_export_flat", Destructive = false, OpenWorld = false)]
    [Description("Export a saved sheet metal part's flat pattern to DXF or DWG inside the Datum folder. For a DXF the reply gives the outline size read back from the file.")]
    public static Task<CallToolResult> ExportFlat(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Output file, e.g. 'bracket.dxf'; relative paths go in the Datum folder")] string path,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var full = workspace.ResolveWritePath(path);
                if (Path.GetExtension(full).ToUpperInvariant() is not (".DXF" or ".DWG"))
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"can't export a flat pattern to '{Path.GetExtension(full)}'", "Use .dxf or .dwg");
                }

                await client.RunAsync(b => { b.ExportFlatPattern(target.Id, full); return true; }, SheetTimeout, cancellationToken).ConfigureAwait(false);
                if (!File.Exists(full))
                {
                    throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS reported the export done, but '{full}' is missing");
                }

                var outline = Path.GetExtension(full).Equals(".dxf", StringComparison.OrdinalIgnoreCase) ? Dxf.Extents(await File.ReadAllTextAsync(full, cancellationToken).ConfigureAwait(false)) : null;
                return $"Exported the flat pattern to {full}{(outline is { } size ? $", outline {Numbers.Format(size.Width)} x {Numbers.Format(size.Height)} mm" : "")}\n";
            },
            cancellationToken));
}
