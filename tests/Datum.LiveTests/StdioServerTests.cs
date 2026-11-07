using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public class StdioServerTests
{
    [Fact]
    public async Task ServesSwStatusOverStdio()
    {
        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions { Name = "datum", Command = DatumExecutable() }));

        var tools = await client.ListToolsAsync();
        var status = await client.CallToolAsync("sw_status");

        Assert.Equal(
            ["checkpoint_list", "checkpoint_restore", "doc_activate", "doc_allow_edit", "doc_close", "doc_export", "doc_list", "doc_new", "doc_open", "doc_save", "feature_extrude", "find_tools", "model_faces", "model_info", "model_select", "model_tree", "selection_get", "sketch_add", "sketch_begin", "sketch_constrain", "sketch_end", "sw_status", "toolset_enable", "view_capture"],
            tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.NotEqual(true, status.IsError);
        Assert.StartsWith("SOLIDWORKS 20", TextOf(status), StringComparison.Ordinal);
        Assert.Contains("License: ", TextOf(status), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASessionsIntentReplaysToTheSamePart()
    {
        var file = Path.Combine(Path.GetTempPath(), $"datum-intent-{Guid.NewGuid():N}.json");
        var options = new StdioClientTransportOptions { Name = "datum", Command = DatumExecutable(), Arguments = ["--allow", Path.GetTempPath()] };
        static Dictionary<string, object?> Args(params (string Key, object? Value)[] args) => args.ToDictionary(a => a.Key, a => a.Value);
        string info;
        await using (var first = await McpClient.CreateAsync(new StdioClientTransport(options)))
        {
            await first.CallToolAsync("toolset_enable", Args(("name", "intent")));
            await first.CallToolAsync("doc_new", Args(("type", "part")));
            await first.CallToolAsync("sketch_begin", Args(("on", "Front")));
            await first.CallToolAsync("sketch_add", Args(("entities", new object[] { new Dictionary<string, object?> { ["type"] = "center_rect", ["cx"] = 0, ["cy"] = 0, ["width"] = 70, ["height"] = 30 } })));
            var sketch = TextOf(await first.CallToolAsync("sketch_end"));
            await first.CallToolAsync("feature_extrude", Args(("sketch", System.Text.RegularExpressions.Regex.Match(sketch, @"feat#\d+").Value), ("depth", 12)));
            await first.CallToolAsync("toolset_enable", Args(("name", "features")));
            var top = TextOf(await first.CallToolAsync("model_faces", Args(("normal", "+z"), ("limit", 1)))).Split(' ')[0];
            await first.CallToolAsync("sketch_begin", Args(("on", top)));
            await first.CallToolAsync("sketch_add", Args(("entities", new object[] { new Dictionary<string, object?> { ["type"] = "circle", ["cx"] = 10, ["cy"] = 0, ["radius"] = 4 } })));
            var hole = TextOf(await first.CallToolAsync("sketch_end"));
            await first.CallToolAsync("feature_extrude", Args(("sketch", System.Text.RegularExpressions.Regex.Match(hole, @"feat#\d+").Value), ("cut", true), ("end", "through_all")));
            var edge = TextOf(await first.CallToolAsync("model_faces", Args(("kind", "edge"), ("direction", "z"), ("limit", 1)))).Split(' ')[0];
            Assert.False(TextOf(await first.CallToolAsync("feature_fillet", Args(("edges", new[] { edge }), ("radius", 3)))).StartsWith("INVALID", StringComparison.Ordinal));
            info = TextOf(await first.CallToolAsync("model_info"));
            Assert.Contains("Wrote 12 steps", TextOf(await first.CallToolAsync("intent_export", Args(("path", file)))), StringComparison.Ordinal);
            await first.CallToolAsync("doc_close", Args(("discardChanges", true)));
        }

        await using var second = await McpClient.CreateAsync(new StdioClientTransport(options));
        await Datum.Core.IntentLog.ReplayAsync(Datum.Core.IntentLog.Import(await File.ReadAllTextAsync(file)), async (tool, args) =>
        {
            var reply = await second.CallToolAsync(tool, args.ToDictionary(a => a.Key, a => (object?)a.Value));
            Assert.NotEqual(true, reply.IsError);
            return TextOf(reply);
        });
        var replayed = TextOf(await second.CallToolAsync("model_info"));
        await second.CallToolAsync("doc_close", Args(("discardChanges", true)));
        File.Delete(file);

        Assert.Equal(info[(info.IndexOf('\n', StringComparison.Ordinal) + 1)..], replayed[(replayed.IndexOf('\n', StringComparison.Ordinal) + 1)..]);
    }

    [Fact]
    public async Task ListsAddInGuidsThroughTheDiagnosticsToolset()
    {
        await using var client = await McpClient.CreateAsync(
            new StdioClientTransport(new StdioClientTransportOptions { Name = "datum", Command = DatumExecutable() }));

        await client.CallToolAsync("toolset_enable", new Dictionary<string, object?> { ["name"] = "diagnostics" });
        var addIns = await client.CallToolAsync("sw_addins");

        Assert.NotEqual(true, addIns.IsError);
        // Which add-ins are loaded depends on how SOLIDWORKS was started (automation skips some), so only the listing is checked.
        Assert.Matches(@"\[[x ]\] 3DEXPERIENCE Marketplace \{219180b0-7183-4fe2-b167-4e2bfe534004\}", TextOf(addIns));
    }

    private static string TextOf(CallToolResult result) => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    // Tests run from tests/Datum.LiveTests/bin/<Configuration>/<tfm>/; use the server built in the same configuration.
    private static string DatumExecutable()
    {
        var output = new DirectoryInfo(AppContext.BaseDirectory);
        var configuration = output.Parent!.Name;
        var root = output;
        while (!File.Exists(Path.Combine(root.FullName, "datum.slnx")))
        {
            root = root.Parent ?? throw new DirectoryNotFoundException("Couldn't find datum.slnx above the test output.");
        }

        var exe = Path.Combine(root.FullName, "src", "Datum.Server", "bin", configuration, output.Name, "datum.exe");
        return File.Exists(exe) ? exe : throw new FileNotFoundException($"Build Datum.Server ({configuration}) first.", exe);
    }
}
