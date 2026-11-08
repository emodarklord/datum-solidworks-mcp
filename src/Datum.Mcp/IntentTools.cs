using System.ComponentModel;
using System.Text.Json;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class IntentTools
{
    private const string Toolset = "intent";

    [Toolset(Toolset)]
    [McpServerTool(Name = "intent_export", Destructive = false, OpenWorld = false)]
    [Description("Write this session's modelling steps to a JSON file in the Datum folder, in the order they were made. Gateway calls are tagged solidworks. Faces and edges are recorded by their geometry, so a replay in a new session finds them again. Steps on assembly components can't be replayed yet.")]
    public static async Task<CallToolResult> Export(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Output file, e.g. 'bracket.intent.json'; relative paths go in the Datum folder")] string path,
        CancellationToken cancellationToken = default)
    {
        return await ToolErrors.Run(async () =>
        {
            var full = workspace.ResolveWritePath(path);
            await File.WriteAllTextAsync(full, solidWorks.Intent.Export(), cancellationToken).ConfigureAwait(false);
            return $"Wrote {solidWorks.Intent.Steps.Count} steps to {full}\n";
        }).ConfigureAwait(false);
    }

    [Toolset(Toolset)]
    [McpServerTool(Name = "recipe_run", Destructive = false, OpenWorld = false)]
    [Description("Replay a saved recipe (a session written by intent_export; datum://recipes lists them) step by step. A recipe usually starts with doc_new, so it builds a new document. Faces and edges are found again by their geometry. Stops at the first step that fails, with what was made so far left in place.")]
    public static async Task<CallToolResult> Run(
        McpServer server,
        ToolCatalog catalog,
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Recipe name, e.g. 'bracket' for bracket.intent.json in the Datum folder")] string name,
        CancellationToken cancellationToken = default)
    {
        return await ToolErrors.Run(async () =>
        {
            var file = Recipes.PathOf(workspace.AllowedFolders[0], name);
            var steps = Recipes.Read(file);
            var step = 0;
            var last = "";
            await IntentLog.ReplayAsync(steps, async (tool, args) =>
            {
                var recorded = IntentLog.Records(tool);
                step += recorded ? 1 : 0;
                var found = catalog.Tool(tool) ?? throw new DatumException(ErrorCode.NotFound, $"step {step} calls {tool}, which this Datum doesn't have", "Update Datum, or record the recipe again");
                if (catalog.ReadOnlyRefusal(tool) is { } refusal)
                {
                    throw refusal;
                }

                var refs = recorded ? await InspectionTools.DescribeAsync(solidWorks, IntentLog.LooseHandles(JsonSerializer.Serialize(args)), cancellationToken).ConfigureAwait(false) : [];
                var request = new CallToolRequestParams { Name = tool, Arguments = args };
                var reply = await found.InvokeAsync(new RequestContext<CallToolRequestParams>(server, new JsonRpcRequest { Method = RequestMethods.ToolsCall }, request) { Services = server.Services }, cancellationToken).ConfigureAwait(false);
                var text = string.Concat(reply.Content.OfType<TextContentBlock>().Select(block => block.Text));
                if (reply.IsError == true)
                {
                    throw new DatumException(ErrorCode.FeatureFailed, $"step {step} of {steps.Count} ({tool}) failed: {text.Trim()}", "Fix the model by hand or edit the recipe, then run the rest yourself");
                }

                if (recorded)
                {
                    solidWorks.Intent.Add(tool, args, text, refs);
                    last = text;
                }

                return text;
            }).ConfigureAwait(false);
            return $"Ran {Recipes.NameOf(file)}: {steps.Count} steps\nLast step: {last}";
        }).ConfigureAwait(false);
    }
}
