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

    private static readonly TimeSpan TeachTimeout = TimeSpan.FromSeconds(90);

    [Toolset(Toolset)]
    [McpServerTool(Name = "teach_start", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false)]
    [Description("Start watching a part while the user models by hand in SOLIDWORKS. teach_stop then turns what they made into a recipe recipe_run can replay. Datum can learn sketches of lines, arcs, circles and centerlines on Front, Top, Right or a flat face, extrudes and cuts (blind, through all, mid plane), revolves and fillets on edges.")]
    public static Task<CallToolResult> TeachStart(
        SolidWorksConnection solidWorks,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                if (target.Type != DocumentType.Part)
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"{target.Title} is not a part", "Teach on a part");
                }

                var features = await client.RunAsync(b => b.ListFeatures(target.Id), TeachTimeout, cancellationToken).ConfigureAwait(false);
                var model = await client.RunAsync(b => b.ReadModel(target.Id), TeachTimeout, cancellationToken).ConfigureAwait(false);
                solidWorks.Teaching[target.Id] = new TeachingStart(features.Select(f => f.Handle).ToHashSet(), model.Bodies == 0);
                return $"Watching {target.Title}{(model.Bodies == 0 ? ", from an empty part" : $", which already has {model.Bodies} body; the recipe will need a part like it")}. Model by hand, then call teach_stop with a name\n";
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "teach_stop", Destructive = false, OpenWorld = false)]
    [Description("Turn the features made since teach_start into a recipe, <name>.intent.json in the Datum folder, and list any feature Datum couldn't learn. Faces and edges are recorded by their geometry in the finished part.")]
    public static Task<CallToolResult> TeachStop(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Recipe name, e.g. 'flange'")] string name,
        [Description("Replace a recipe of that name (it is backed up first)")] bool overwrite = false,
        [Description("Document id, title or path; defaults to the active document")] string? doc = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var target = await DocumentTools.ResolveAsync(client, doc, cancellationToken).ConfigureAwait(false);
                if (!solidWorks.Teaching.TryGetValue(target.Id, out var start))
                {
                    throw new DatumException(ErrorCode.InvalidArgument, $"{target.Title} isn't being watched", "Call teach_start before modelling");
                }

                var made = (await client.RunAsync(b => b.ListFeatures(target.Id), TeachTimeout, cancellationToken).ConfigureAwait(false))
                    .Select(f => f.Handle).Where(h => !start.Features.Contains(h)).ToList();
                var lesson = await client.RunAsync(b => b.ReadLesson(target.Id, made), TeachTimeout, cancellationToken).ConfigureAwait(false);
                if (lesson.Steps.Count == 0)
                {
                    throw new DatumException(ErrorCode.NotFound, $"nothing Datum can learn since teach_start{(lesson.Skipped.Count == 0 ? "" : $"; it can't learn {string.Join(", ", lesson.Skipped)}")}", "Make sketches, extrudes, revolves or fillets, then call teach_stop again");
                }

                var steps = new List<IntentStep>();
                if (start.FromScratch)
                {
                    steps.Add(new IntentStep("doc_new", new Dictionary<string, JsonElement> { ["type"] = JsonSerializer.SerializeToElement("part") }, null, "neutral"));
                }

                var faces = lesson.Faces.DistinctBy(f => f.Handle).ToDictionary(f => f.Handle, InspectionTools.FaceKey, StringComparer.OrdinalIgnoreCase);
                var edges = lesson.Edges.ToDictionary(
                    e => e.Handle,
                    e => $"edge:{InspectionTools.EdgeKey(e)}|{string.Join("|", e.Faces.Select(f => faces.GetValueOrDefault(f, "")))}",
                    StringComparer.OrdinalIgnoreCase);
                foreach (var taught in lesson.Steps)
                {
                    var args = taught.Args.ToDictionary(pair => pair.Key, pair => JsonSerializer.SerializeToElement(pair.Value));
                    var refs = IntentLog.LooseHandles(JsonSerializer.Serialize(args))
                        .Select(h => (Handle: h, Described: faces.TryGetValue(h, out var face) ? $"face:{face}" : edges.GetValueOrDefault(h)))
                        .Where(r => r.Described is not null)
                        .ToDictionary(r => r.Handle, r => r.Described!, StringComparer.OrdinalIgnoreCase);
                    steps.Add(new IntentStep(taught.Tool, args, taught.Made, "neutral") { Refs = refs });
                }

                var full = workspace.ResolveWritePath(name.EndsWith(Recipes.Extension, StringComparison.OrdinalIgnoreCase) ? name : name + Recipes.Extension);
                var backup = DocumentTools.Replace(workspace, full, overwrite);
                await File.WriteAllTextAsync(full, IntentLog.Export(steps), cancellationToken).ConfigureAwait(false);
                solidWorks.Teaching.Remove(target.Id);
                var skipped = lesson.Skipped.Count == 0 ? "" : $"Couldn't learn: {string.Join(", ", lesson.Skipped)}. Add those by hand after a replay\n";
                return $"Wrote {steps.Count} steps to {full}{DocumentTools.BackupNote(backup)}; recipe_run {Recipes.NameOf(full)} replays it\n{skipped}";
            },
            cancellationToken));

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
