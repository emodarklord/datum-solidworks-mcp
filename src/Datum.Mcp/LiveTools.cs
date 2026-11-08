using System.ComponentModel;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class LiveTools
{
    private const string Toolset = "live";

    [Toolset(Toolset)]
    [McpServerTool(Name = "events_recent", ReadOnly = true, Destructive = false, OpenWorld = false)]
    [Description("What happened in SOLIDWORKS while you weren't calling it: the active document changing, files opened, created, saved or closed, rebuilds with any errors, and the user selecting things. Newest last.")]
    public static Task<CallToolResult> Recent(
        SolidWorksConnection solidWorks,
        [Description("How many events")] int limit = 20,
        [Description("Only this kind: active, open, new, rebuild, selection, save or close")] string? kind = null,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            _ =>
            {
                var events = solidWorks.Events.Recent(Math.Clamp(limit, 1, 200), kind);
                return Task.FromResult(events.Count == 0 ? "Nothing yet. Events are recorded from Datum's first call onwards\n" : string.Concat(events.Select(e => $"{e}\n")));
            },
            cancellationToken));
}
