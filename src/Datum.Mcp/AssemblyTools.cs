using System.ComponentModel;
using System.Globalization;
using System.Text;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class AssemblyTools
{
    private const string Toolset = "assembly";

    private static readonly TimeSpan AssemblyTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan InterferenceTimeout = TimeSpan.FromMinutes(5);

    [Toolset(Toolset)]
    [McpServerTool(Name = "component_add", Destructive = false, OpenWorld = false)]
    [Description("Add a part or sub-assembly file to the active assembly and return its comp# handle. Position in mm in assembly coordinates. "
        + "The position is where the part's own origin lands. SOLIDWORKS fixes the first component added. The file itself isn't changed. Create the assembly first with doc_new.")]
    public static Task<CallToolResult> Add(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("The .sldprt or .sldasm file; relative paths are looked up in the Datum folder")] string path,
        [Description("X position in mm")] double x = 0,
        [Description("Y position in mm")] double y = 0,
        [Description("Z position in mm")] double z = 0,
        [Description("The component's configuration; defaults to its active one")] string? configuration = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAssemblyAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var file = workspace.ResolveReadPath(path);
                var added = await client.RunAsync(b => b.AddComponent(target.Id, file, new Point3(x, y, z), configuration), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"before adding {added.Name}", new RemoveComponent(added.Handle));
                return $"Added {added}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "component_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List an assembly's top-level components: comp# handle, name, configuration, whether fixed, under defined or fully defined, and position.")]
    public static Task<CallToolResult> List(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var components = await client.RunAsync(b => b.ListComponents(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                return components.Count == 0 ? "No components\n" : string.Concat(components.Select(c => $"{c}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "component_fix", Destructive = false, OpenWorld = false)]
    [Description("Fix a component in place, or float it so it can move. Fix one base component before mating the others to it.")]
    public static Task<CallToolResult> Fix(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Component handle, comp#N")] string component,
        [Description("true to fix, false to float")] bool isFixed = true,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAssemblyAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var changed = await client.RunAsync(b => b.SetComponentFixed(target.Id, component, isFixed), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"{(isFixed ? "fixed" : "floated")} {changed.Name}", new RestoreFixed(component, !isFixed));
                return $"{changed}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "component_flexible", Destructive = false, OpenWorld = false)]
    [Description("Make a sub-assembly flexible, so its own mates still move inside this assembly, as gears or linkages in a bought-in motor need; or rigid again, so it moves as one piece.")]
    public static Task<CallToolResult> Flexible(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Sub-assembly component handle, comp#N")] string component,
        [Description("true for flexible, false for rigid")] bool flexible = true,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAssemblyAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var changed = await client.RunAsync(b => b.SetComponentFlexible(target.Id, component, flexible), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"made {changed.Name} {(flexible ? "flexible" : "rigid")}", new RestoreFlexible(component, !flexible));
                return $"{changed}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "component_move", Destructive = false, OpenWorld = false)]
    [Description("Place a component at a position in mm and rotation in degrees, about the assembly's X, then Y, then Z. "
        + "The result is read back: if mates hold it elsewhere, the reply says so. A fixed component is refused until floated. Prefer mates for final positions.")]
    public static Task<CallToolResult> Move(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Component handle, comp#N")] string component,
        [Description("X position in mm")] double x,
        [Description("Y position in mm")] double y,
        [Description("Z position in mm")] double z,
        [Description("Rotation about X in degrees")] double rx = 0,
        [Description("Rotation about Y in degrees")] double ry = 0,
        [Description("Rotation about Z in degrees")] double rz = 0,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await EditableAssemblyAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var pose = new ComponentPose(new Point3(x, y, z), new Point3(rx, ry, rz));
                var move = await client.RunAsync(b => b.MoveComponent(target.Id, component, pose), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"before moving {component}", new RestorePose(component, move.Before));
                return move.Held
                    ? $"{component} didn't reach the requested pose; mates hold it {move.After}\n"
                    : $"{component} moved {move.After}\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "mate_add", Destructive = false, OpenWorld = false)]
    [Description("Mate two entities: face# or edge# handles from model_faces with component set, a component's plane such as comp#5:front, or the assembly's Front, Top or Right. "
        + "A mate that fails or breaks the rebuild is removed and everything is put back. The reply lists what moved and what is still free. Distances in mm, angles in degrees.")]
    public static Task<CallToolResult> Mate(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("coincident, concentric, parallel, perpendicular, tangent, distance, angle, lock, gear, screw or profile_center. Gear ties two cylinders' rotation at a ratio. Screw ties one part's turning to the other's travel along two concentric cylinders. Profile_center centres one face's outline on another's, such as a hex peg in a hex socket, with value as an offset")] string kind,
        [Description("First entity, or \"selected\" for the first thing selected in SOLIDWORKS")] string first,
        [Description("Second entity, or \"selected\" for the next selected thing")] string second,
        [Description("Distance in mm or angle in degrees, for those two kinds. For screw, the lead in mm per turn")] double? value = null,
        [Description("closest, aligned or anti_aligned")] string alignment = "closest",
        [Description("Put a distance or angle on the other side, or reverse a screw")] bool flip = false,
        [Description("Lowest distance or angle, making it a range with value as the start")] double? min = null,
        [Description("Highest distance or angle")] double? max = null,
        [Description("Gear ratio as first:second size, so the second turns faster when the first is bigger, e.g. 60:13 teeth")] string? ratio = null,
        [Description("Profile_center only: stop the second part turning about the first")] bool lockRotation = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var spec = new MateSpec(kind, first, second, value, alignment, flip) { Min = min, Max = max, Ratio = ratio, LockRotation = lockRotation };
                spec.Validate();
                var target = await EditableAssemblyAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var mate = await client.RunAsync(b => b.AddMate(target.Id, spec), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"before {mate.Name}", new RemoveMate(mate.Handle, mate.Moved));
                var report = new StringBuilder($"{mate.Name} is {mate.Handle}\n");
                foreach (var moved in mate.Moved)
                {
                    report.Append(CultureInfo.InvariantCulture, $"{moved.Handle} {moved.Name} moved {moved.After}\n");
                }

                report.Append(mate.Unconstrained.Count == 0 ? "All components are fixed or fully defined\n" : $"Still free to move: {string.Join(", ", mate.Unconstrained)}\n");
                return (spec.Limited ? report.Append("The limits leave its components free to move within the range, even if they show as fully defined\n") : report).ToString();
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "mate_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List an assembly's mates with their kind, the components they tie together, and any error.")]
    public static Task<CallToolResult> Mates(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var mates = await client.RunAsync(b => b.ListMates(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                return mates.Count == 0
                    ? "No mates\n"
                    : string.Concat(mates.Select(m => $"{m.Handle} {m.Name} [{m.Label}] {string.Join(" to ", m.Components)}{(m.Suppressed ? " suppressed" : "")}{(m.Error is null ? "" : $" ERROR: {m.Error}")}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "assembly_check", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Check an assembly: overlapping components with their volumes, components still free to move, mate errors, suppressed components and missing files. "
        + "Interference is checked at the current pose only, and an empty list doesn't mean the assembly meets its requirements.")]
    public static Task<CallToolResult> Check(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var components = await client.RunAsync(b => b.ListComponents(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                var mates = await client.RunAsync(b => b.ListMates(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                var clashes = await client.RunAsync(b => b.FindInterference(target.Id), InterferenceTimeout, cancellationToken).ConfigureAwait(false);
                var missing = await client.RunAsync(b => b.MissingFiles(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                var report = new StringBuilder($"{target.Title}: {components.Count} components, {mates.Count} mates, {clashes.Count} overlaps at this pose\n");
                AppendProblems(report, AssemblyProblems.Find(components, mates, clashes, missing));
                if (await client.RunAsync(b => b.ActiveMotionStudy(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false) is { } study)
                {
                    report.Append(CultureInfo.InvariantCulture, $"Read while motion study '{study}' is showing, where SOLIDWORKS can call movable parts no solution. If that looks wrong, click the Model tab under the graphics area and check again\n");
                }

                return report.ToString();
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "assembly_bom", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List the parts in an assembly with their quantities, including those inside sub-assemblies, plus each part's Description and PartNo properties. Suppressed components aren't counted.")]
    public static Task<CallToolResult> Bom(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var lines = await client.RunAsync(b => b.ReadBom(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                return lines.Count == 0
                    ? "No parts\n"
                    : string.Concat(lines.Select(l => $"{l.Quantity} x {l.File} [{l.Configuration}]{(l.PartNumber is null ? "" : $" PartNo {l.PartNumber}")}{(l.Description is null ? "" : $" {l.Description}")}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "config_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List a part's or assembly's configurations and which one is active.")]
    public static Task<CallToolResult> Configurations(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var configurations = await client.RunAsync(b => b.ListConfigurations(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                return string.Concat(configurations.Select(c => $"{c.Name}{(c.Active ? " (active)" : "")}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "config_add", Destructive = false, OpenWorld = false)]
    [Description("Add a configuration and make it active. Checkpoints can't restore past this.")]
    public static Task<CallToolResult> AddConfiguration(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("New configuration name")] string name,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                DocumentTools.RequireEditable(workspace, target);
                var active = await client.RunAsync(b => b.AddConfiguration(target.Id, name), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"added configuration {active}", new NoReversal("adding a configuration"));
                return $"Configuration {active} added and active\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "config_activate", Destructive = false, OpenWorld = false)]
    [Description("Make a configuration active and confirm it. This only changes what's shown until the document is saved, so it works on read-only documents too. comp#, face# and edge# handles keep working afterwards.")]
    public static Task<CallToolResult> ActivateConfiguration(
        SolidWorksConnection solidWorks,
        [Description("Configuration name")] string name,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                return $"Configuration {await client.RunAsync(b => b.ActivateConfiguration(target.Id, name), AssemblyTimeout, cancellationToken).ConfigureAwait(false)} is active\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "explode_create", Destructive = false, OpenWorld = false)]
    [Description("Make an exploded view in the active configuration: each step moves one component along the assembly's x, y or z axis. "
        + "Use config_add first so the normal configuration stays as it is. Mates aren't affected. Checkpoints can't restore past this.")]
    public static Task<CallToolResult> Explode(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("The steps, in order")] ExplodeStepSpec[] steps,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                ExplodeStepSpec.Validate(steps);
                var target = await EditableAssemblyAsync(client, workspace, doc, cancellationToken).ConfigureAwait(false);
                var result = await client.RunAsync(b => b.CreateExplode(target.Id, steps), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Checkpoints.Record(target.Id, $"exploded {result.Configuration}", new NoReversal("an exploded view"));
                var report = new StringBuilder($"{result.View} in configuration {result.Configuration}, {result.Steps.Count} {(result.Steps.Count == 1 ? "step" : "steps")}\n");
                foreach (var step in result.Steps)
                {
                    report.Append(CultureInfo.InvariantCulture, $"{step.Name}: {step.Component} by {Numbers.Format(step.DistanceMm)} mm\n");
                }

                return report.ToString();
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "explode_show", Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Show the active configuration's exploded view, or collapse it, and confirm the result. Works on read-only documents.")]
    public static Task<CallToolResult> ShowExplode(
        SolidWorksConnection solidWorks,
        [Description("true to explode, false to collapse")] bool show = true,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var exploded = await client.RunAsync(b => b.ShowExplode(target.Id, show), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
                return exploded ? "Exploded\n" : "Collapsed\n";
            },
            cancellationToken));

    internal static async Task<string> SummaryAsync(SolidWorksClient client, DocumentInfo target, CancellationToken cancellationToken)
    {
        var model = await client.RunAsync(b => b.ReadModel(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
        var components = await client.RunAsync(b => b.ListComponents(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
        var mates = await client.RunAsync(b => b.ListMates(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
        var clashes = await client.RunAsync(b => b.FindInterference(target.Id), InterferenceTimeout, cancellationToken).ConfigureAwait(false);
        var missing = await client.RunAsync(b => b.MissingFiles(target.Id), AssemblyTimeout, cancellationToken).ConfigureAwait(false);
        var report = new StringBuilder($"{model.Title}: {components.Count} components, {mates.Count} mates, {model.Bodies} bodies\n")
            .Append(CultureInfo.InvariantCulture, $"Mass {Numbers.Format(model.MassGrams)} g · volume {Numbers.Format(model.VolumeMm3)} mm³");
        if (model.Box is { Size: var size })
        {
            report.Append(CultureInfo.InvariantCulture, $" · box {Numbers.Format(size.X)} x {Numbers.Format(size.Y)} x {Numbers.Format(size.Z)} mm");
        }

        report.Append("\nComponents:\n");
        foreach (var component in components)
        {
            report.Append(CultureInfo.InvariantCulture, $"{component}\n");
        }

        report.Append(mates.Count == 0 ? "No mates\n" : "Mates:\n");
        foreach (var mate in mates)
        {
            report.Append(CultureInfo.InvariantCulture, $"{mate.Handle} {mate.Name} [{mate.Label}] {string.Join(" to ", mate.Components)}\n");
        }

        AppendProblems(report, AssemblyProblems.Find(components, mates, clashes, missing));
        return report.ToString();
    }

    internal static async Task<DocumentInfo> AssemblyAsync(SolidWorksClient client, string? doc, CancellationToken cancellationToken)
    {
        var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
        return target.Type == DocumentType.Assembly
            ? target
            : throw new DatumException(ErrorCode.InvalidArgument, $"{target.Id} is not an assembly", "Call doc_new with type assembly, or doc_activate an open one");
    }

    // Parts left free to move are what a mechanism is made of, so they aren't problems.
    private static void AppendProblems(StringBuilder report, IReadOnlyList<string> found)
    {
        var free = found.Where(AssemblyProblems.IsFreeToMove).ToList();
        var problems = found.Except(free).ToList();
        report.Append(problems.Count == 0 ? "No problems found\n" : $"Problems ({problems.Count}):\n");
        foreach (var problem in problems)
        {
            report.Append(CultureInfo.InvariantCulture, $"- {problem}\n");
        }

        if (free.Count > 0)
        {
            report.Append(CultureInfo.InvariantCulture, $"Free to move, as expected in a mechanism ({free.Count}):\n");
            foreach (var part in free)
            {
                report.Append(CultureInfo.InvariantCulture, $"- {part}\n");
            }
        }
    }

    private static async Task<DocumentInfo> EditableAssemblyAsync(SolidWorksClient client, Workspace workspace, string? doc, CancellationToken cancellationToken)
    {
        var target = await AssemblyAsync(client, doc, cancellationToken).ConfigureAwait(false);
        DocumentTools.RequireEditable(workspace, target);
        return target;
    }
}
