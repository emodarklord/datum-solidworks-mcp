using System.ComponentModel;
using System.Text;
using System.Text.Json;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class ApiTools
{
    private const string Toolset = "api";

    private static readonly TimeSpan GatewayTimeout = TimeSpan.FromSeconds(60);

    [Toolset(Toolset)]
    [McpServerTool(Name = "api_search", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Find SOLIDWORKS API members from a plain description, such as 'thicken surface'. Searches the running SOLIDWORKS's own API by name and shows only each member's newest version. Follow up with api_members and invoke.")]
    public static Task<CallToolResult> Search(
        SolidWorksConnection solidWorks,
        [Description("What you want to do, in a few words")] string query,
        [Description("Most results to return")] int limit = 10,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var entries = await client.RunAsync(b => b.ApiEntries(), GatewayTimeout, cancellationToken).ConfigureAwait(false);
                var found = ApiSearch.Rank(query, entries, limit);
                return found.Count == 0 ? $"Nothing in the API matches '{query}'\n" : string.Concat(found.Select(entry => $"{entry.Interface}.{entry.Signature}\n"));
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "run_macro", Destructive = true, OpenWorld = true)]
    [Description("Last resort: run a SOLIDWORKS macro file from the Datum folder. Off unless datum was started with --allow-macros. A macro can do anything the user can, and checkpoints can't undo it.")]
    public static Task<CallToolResult> RunMacro(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        DatumOptions options,
        [Description("Macro file, .swp or .dll")] string path,
        [Description("Module name")] string module,
        [Description("Procedure name")] string procedure,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                if (!options.AllowMacros || options.ReadOnly)
                {
                    throw new DatumException(ErrorCode.NotAllowed, "macros are off", "Start datum with --allow-macros");
                }

                var full = workspace.ResolveWritePath(path);
                if (!File.Exists(full))
                {
                    throw new DatumException(ErrorCode.NotFound, $"no macro at {full}", "Put it in the Datum folder");
                }

                await client.RunAsync(b => { b.RunMacro(full, module, procedure); return true; }, GatewayTimeout, cancellationToken).ConfigureAwait(false);
                return $"Ran {procedure} from {Path.GetFileName(full)}. Checkpoints can't undo it\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "api_members", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("List the members of a SOLIDWORKS API interface as signatures, read from the running SOLIDWORKS's own API. "
        + "Pass an interface name (IFeature, IModelDoc2, ISimpleFilletFeatureData2) or a handle to list its object's interface. "
        + "Use it with invoke to reach anything the curated tools don't cover.")]
    public static Task<CallToolResult> Members(
        SolidWorksConnection solidWorks,
        [Description("Interface name, or a handle such as feat#3 or obj#7")] string type,
        [Description("Only members whose name contains this")] string? query = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var id = await DocumentIdAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var members = await client.RunAsync(b => b.DescribeMembers(id, type, query), GatewayTimeout, cancellationToken).ConfigureAwait(false);
                if (members.Count == 0)
                {
                    return $"No members of {type} match '{query}'\n";
                }

                var report = new StringBuilder($"{members[0].Interface}: {members.Count} members\n");
                foreach (var member in members)
                {
                    report.Append(member).Append('\n');
                }

                return report.ToString();
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "invoke", Destructive = true, OpenWorld = false)]
    [Description("Call any SOLIDWORKS API member that has no curated tool: a method, a property read, or a property write (pass value). "
        + "target is app, doc, or a handle (feat#, face#, edge#, obj#). Numbers are SOLIDWORKS units, metres and radians, unless written "
        + "with a unit like \"3 mm\" or \"30 deg\"; enums go by name (swEndCondBlind); API objects go by handle, and \"doc\" or \"app\" stand for the document or SOLIDWORKS; null passes Nothing. Object results come back "
        + "as handles for further calls. Reads always work. Calls that may change the model need datum started with --allow-invoke-writes, "
        + "and Datum can't undo them, so a checkpoint restore can't go back past one. Look members up with api_members first.")]
    public static Task<CallToolResult> Invoke(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        DatumOptions options,
        [Description("app, doc, or a handle such as feat#3, face#12 or obj#7")] string target,
        [Description("Method or property name, e.g. GetDefinition or DefaultRadius")] string member,
        [Description("Arguments in order; out parameters at the end can be left off")] JsonElement[]? args = null,
        [Description("Set the property to this instead of reading it")] JsonElement? value = null,
        [Description("The interface an object result should have, e.g. ISimpleFilletFeatureData2; saves Datum finding it")] string? @as = null,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var id = await DocumentIdAsync(client, doc, cancellationToken).ConfigureAwait(false);
                var writes = !options.AllowInvokeWrites || options.ReadOnly ? GatewayWrites.Disabled
                    : id is not null && !workspace.IsEditable(id) ? GatewayWrites.NotOwned
                    : GatewayWrites.Allowed;
                var request = new ApiCall(target, member, args ?? [], value, @as);
                var result = await client.RunAsync(b => b.Invoke(id, request, writes), GatewayTimeout, cancellationToken).ConfigureAwait(false);
                if (result.Wrote && id is not null)
                {
                    solidWorks.Checkpoints.Record(id, $"invoke {target} {member}", new NoReversal($"a gateway call to {member}"));
                }

                var reply = new StringBuilder($"{target}.{member} → {result.Value}");
                foreach (var output in result.Outs)
                {
                    reply.Append(" · ").Append(output);
                }

                if (result.Change is { } change)
                {
                    reply.Append(" · ").Append(change);
                }

                if (result.Wrote)
                {
                    reply.Append(" · Datum can't undo this, so checkpoints can't restore past it");
                }

                return reply.Append('\n').ToString();
            },
            cancellationToken));

    // The gateway works without a document too (target app), so a missing active document isn't an error here.
    private static async Task<string?> DocumentIdAsync(SolidWorksClient client, string? doc, CancellationToken cancellationToken)
    {
        if (doc is not null)
        {
            return (await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false)).Id;
        }

        var documents = await client.RunAsync(b => b.ListDocuments(), cancellationToken: cancellationToken).ConfigureAwait(false);
        return documents.FirstOrDefault(d => d.Active)?.Id;
    }
}
