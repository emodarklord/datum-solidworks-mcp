using System.ComponentModel;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class EquationTools
{
    private const string Toolset = "equations";

    private static readonly TimeSpan EquationTimeout = TimeSpan.FromSeconds(90);

    [Toolset(Toolset)]
    [McpServerTool(Name = "equation_list", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List a part's or assembly's equations and global variables with their current values in the active configuration, in the document's units, which may be inches even though Datum's tools use mm.")]
    public static Task<CallToolResult> List(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var equations = await client.RunAsync(b => b.ListEquations(target.Id), EquationTimeout, cancellationToken).ConfigureAwait(false);
                return equations.Count == 0 ? "No equations\n" : string.Concat(equations.Select(e => $"{(e.Global ? "variable " : "")}{e.Text} → {Numbers.Format(e.Value)} {e.Unit}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "variable_set", Destructive = false, OpenWorld = false)]
    [Description("Add or change a global variable in mm. Give configurations to set it only there, so one variable can drive different sizes per configuration. Checkpoints can't undo it: change it back or delete it.")]
    public static Task<CallToolResult> Variable(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Variable name, e.g. Depth")] string name,
        [Description("Value in mm")] double value,
        [Description("Configurations to set it in; leave out for all")] string[]? configurations = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        SetAsync(solidWorks, workspace, Equations.Variable(name, value), configurations, doc, cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "equation_set", Destructive = false, OpenWorld = false)]
    [Description("Drive a dimension (a name from model_dimensions) with an expression, e.g. \"Depth\" * 2 or 20mm. Variables go in double quotes and numbers need units. If it breaks the rebuild it is taken back.")]
    public static Task<CallToolResult> Set(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Dimension name, e.g. D1@Boss-Extrude1")] string dimension,
        [Description("Expression, e.g. \"Depth\" * 2")] string expression,
        [Description("Configurations to set it in; leave out for all")] string[]? configurations = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        SetAsync(solidWorks, workspace, Equations.Driving(dimension, expression), configurations, doc, cancellationToken);

    [Toolset(Toolset)]
    [McpServerTool(Name = "equation_delete", Destructive = true, OpenWorld = false)]
    [Description("Delete an equation or global variable by its name. A dimension it drove keeps its last value.")]
    public static Task<CallToolResult> Delete(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Variable or dimension name")] string name,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                DocumentTools.RequireEditable(workspace, target);
                await client.RunAsync(b => { b.DeleteEquation(target.Id, name); return true; }, EquationTimeout, cancellationToken).ConfigureAwait(false);
                return $"Deleted the equation for {name}\n";
            },
            cancellationToken));

    private static Task<CallToolResult> SetAsync(SolidWorksConnection solidWorks, Workspace workspace, string text, string[]? configurations, string? doc, CancellationToken cancellationToken) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                DocumentTools.RequireEditable(workspace, target);
                var before = await client.RunAsync(b => b.ReadModel(target.Id), EquationTimeout, cancellationToken).ConfigureAwait(false);
                var set = await client.RunAsync(b => b.SetEquation(target.Id, text, configurations), EquationTimeout, cancellationToken).ConfigureAwait(false);
                var after = await client.RunAsync(b => b.ReadModel(target.Id), EquationTimeout, cancellationToken).ConfigureAwait(false);
                var where = configurations is { Length: > 0 } ? $" in {string.Join(", ", configurations)}" : "";
                return $"{set.Text}{where} → {Numbers.Format(set.Value)} {set.Unit} · volume {Numbers.Format(before.VolumeMm3)} → {Numbers.Format(after.VolumeMm3)} mm³\n";
            },
            cancellationToken));
}
