using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public class StdioServerTests
{
    private static readonly string[] SelectedEdges = ["selected"];

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
        var file = Path.Combine(Path.GetTempPath(), $"datum-{Guid.NewGuid():N}.intent.json");
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
        await second.CallToolAsync("toolset_enable", Args(("name", "intent")));
        var ran = TextOf(await second.CallToolAsync("recipe_run", Args(("name", Path.GetFileName(file)))));
        Assert.StartsWith("Ran datum-", ran, StringComparison.Ordinal);
        var replayed = TextOf(await second.CallToolAsync("model_info"));
        await second.CallToolAsync("doc_close", Args(("discardChanges", true)));
        File.Delete(file);

        Assert.Equal(info[(info.IndexOf('\n', StringComparison.Ordinal) + 1)..], replayed[(replayed.IndexOf('\n', StringComparison.Ordinal) + 1)..]);
    }

    [Fact]
    public async Task FeaturesMadeWhileTeachingReplayToTheSamePart()
    {
        var folder = Directory.CreateTempSubdirectory("datum-teach-").FullName;
        var options = new StdioClientTransportOptions { Name = "datum", Command = DatumExecutable(), Arguments = ["--allow", folder] };
        static Dictionary<string, object?> Args(params (string Key, object? Value)[] args) => args.ToDictionary(a => a.Key, a => a.Value);
        string info;
        string taught;
        await using (var first = await McpClient.CreateAsync(new StdioClientTransport(options)))
        {
            async Task<string> Call(string tool, Dictionary<string, object?>? args = null)
            {
                var reply = await first.CallToolAsync(tool, args);
                Assert.True(reply.IsError != true, $"{tool}: {TextOf(reply)}");
                return TextOf(reply);
            }

            static string Feat(string reply) => System.Text.RegularExpressions.Regex.Match(reply, @"feat#\d+").Value;
            await Call("toolset_enable", Args(("name", "intent")));
            await Call("toolset_enable", Args(("name", "features")));
            await Call("doc_new", Args(("type", "part")));
            await Call("teach_start");
            await Call("sketch_begin", Args(("on", "Front")));
            await Call("sketch_add", Args(("entities", new object[] { new Dictionary<string, object?> { ["type"] = "center_rect", ["cx"] = 0, ["cy"] = 0, ["width"] = 60, ["height"] = 40 } })));
            await Call("feature_extrude", Args(("sketch", Feat(await Call("sketch_end"))), ("depth", 10)));
            var top = (await Call("model_faces", Args(("normal", "+z"), ("limit", 1)))).Split(' ')[0];
            await Call("sketch_begin", Args(("on", top)));
            await Call("sketch_add", Args(("entities", new object[]
            {
                new Dictionary<string, object?> { ["type"] = "line", ["x1"] = -10, ["y1"] = 0, ["x2"] = 10, ["y2"] = 0 },
                new Dictionary<string, object?> { ["type"] = "arc", ["x1"] = 10, ["y1"] = 0, ["x2"] = -10, ["y2"] = 0, ["x3"] = 0, ["y3"] = 10 },
            })));
            await Call("feature_extrude", Args(("sketch", Feat(await Call("sketch_end"))), ("depth", 6)));
            await Call("sketch_begin", Args(("on", "Front")));
            await Call("sketch_add", Args(("entities", new object[] { new Dictionary<string, object?> { ["type"] = "circle", ["cx"] = 20, ["cy"] = -10, ["radius"] = 4 } })));
            await Call("feature_extrude", Args(("sketch", Feat(await Call("sketch_end"))), ("cut", true), ("end", "through_all")));
            var edge = (await Call("model_faces", Args(("kind", "edge"), ("direction", "z"), ("limit", 1)))).Split(' ')[0];
            await Call("feature_fillet", Args(("edges", new[] { edge }), ("radius", 3)));
            info = await Call("model_info");
            taught = await Call("teach_stop", Args(("name", "lesson")));
            await Call("doc_close", Args(("discardChanges", true)));
        }

        await using var second = await McpClient.CreateAsync(new StdioClientTransport(options));
        await second.CallToolAsync("toolset_enable", Args(("name", "intent")));
        var ran = TextOf(await second.CallToolAsync("recipe_run", Args(("name", "lesson"))));
        var replayed = TextOf(await second.CallToolAsync("model_info"));
        await second.CallToolAsync("doc_close", Args(("discardChanges", true)));
        Directory.Delete(folder, recursive: true);
        Console.WriteLine($"{taught}{ran}\n{info}{replayed}");

        Assert.StartsWith("Wrote 14 steps", taught, StringComparison.Ordinal);
        Assert.DoesNotContain("Couldn't learn", taught, StringComparison.Ordinal);
        Assert.StartsWith("Ran lesson: 14 steps", ran, StringComparison.Ordinal);
        Assert.Equal(info[(info.IndexOf('\n', StringComparison.Ordinal) + 1)..], replayed[(replayed.IndexOf('\n', StringComparison.Ordinal) + 1)..]);
    }

    [Fact]
    public async Task ATaughtRevolveReplays()
    {
        var folder = Directory.CreateTempSubdirectory("datum-teach-").FullName;
        var options = new StdioClientTransportOptions { Name = "datum", Command = DatumExecutable(), Arguments = ["--allow", folder] };
        static Dictionary<string, object?> Args(params (string Key, object? Value)[] args) => args.ToDictionary(a => a.Key, a => a.Value);
        string info;
        string taught;
        await using (var first = await McpClient.CreateAsync(new StdioClientTransport(options)))
        {
            await first.CallToolAsync("toolset_enable", Args(("name", "intent")));
            await first.CallToolAsync("toolset_enable", Args(("name", "features")));
            await first.CallToolAsync("doc_new", Args(("type", "part")));
            await first.CallToolAsync("teach_start");
            await first.CallToolAsync("sketch_begin", Args(("on", "Front")));
            await first.CallToolAsync("sketch_add", Args(("entities", new object[]
            {
                new Dictionary<string, object?> { ["type"] = "polyline", ["closed"] = true, ["points"] = new[] { new[] { 0.0, 0 }, new[] { 30.0, 0 }, new[] { 15.0, 50 }, new[] { 0.0, 50 } } },
                new Dictionary<string, object?> { ["type"] = "centerline", ["x1"] = 0, ["y1"] = -10, ["x2"] = 0, ["y2"] = 60 },
            })));
            var sketch = System.Text.RegularExpressions.Regex.Match(TextOf(await first.CallToolAsync("sketch_end")), @"feat#\d+").Value;
            Assert.NotEqual(true, (await first.CallToolAsync("feature_revolve", Args(("sketch", sketch), ("angle", 270)))).IsError);
            info = TextOf(await first.CallToolAsync("model_info"));
            taught = TextOf(await first.CallToolAsync("teach_stop", Args(("name", "frustum"))));
            await first.CallToolAsync("doc_close", Args(("discardChanges", true)));
        }

        await using var second = await McpClient.CreateAsync(new StdioClientTransport(options));
        await second.CallToolAsync("toolset_enable", Args(("name", "intent")));
        var ran = TextOf(await second.CallToolAsync("recipe_run", Args(("name", "frustum"))));
        var replayed = TextOf(await second.CallToolAsync("model_info"));
        await second.CallToolAsync("doc_close", Args(("discardChanges", true)));
        Directory.Delete(folder, recursive: true);
        Console.WriteLine($"{taught}{ran}\n{info}{replayed}");

        Assert.StartsWith("Wrote 5 steps", taught, StringComparison.Ordinal);
        Assert.StartsWith("Ran frustum: 5 steps", ran, StringComparison.Ordinal);
        Assert.Equal(info[(info.IndexOf('\n', StringComparison.Ordinal) + 1)..], replayed[(replayed.IndexOf('\n', StringComparison.Ordinal) + 1)..]);
    }

    [Fact]
    public async Task AnOlderClientSubscribesAndHearsAboutEvents()
    {
        var options = new StdioClientTransportOptions { Name = "datum", Command = DatumExecutable(), Arguments = ["--allow", Path.GetTempPath()] };
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(options), new McpClientOptions { ProtocolVersion = "2025-11-25" });
        var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await client.CallToolAsync("sw_status");
        await client.SubscribeToResourceAsync("datum://events", (notice, _) =>
        {
            heard.TrySetResult(notice.Uri);
            return ValueTask.CompletedTask;
        });

        await client.CallToolAsync("doc_new", new Dictionary<string, object?> { ["type"] = "part" });
        var uri = await heard.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var events = await client.ReadResourceAsync("datum://events");
        await client.CallToolAsync("doc_close", new Dictionary<string, object?> { ["discardChanges"] = true });

        Assert.Equal("datum://events", uri);
        Assert.Contains(" new ", Assert.IsType<TextResourceContents>(events.Contents[0]).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AListeningClientHearsAboutEvents()
    {
        var options = new StdioClientTransportOptions { Name = "datum", Command = DatumExecutable(), Arguments = ["--allow", Path.GetTempPath()] };
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(options));
        var heard = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var handler = client.RegisterNotificationHandler(NotificationMethods.ResourceUpdatedNotification, (notice, _) =>
        {
            if (notice.Params?.ToString().Contains("datum://events", StringComparison.Ordinal) == true)
            {
                heard.TrySetResult(notice.Params.ToString());
            }

            return ValueTask.CompletedTask;
        });
        await client.CallToolAsync("sw_status");
        using var stop = new CancellationTokenSource();
        var listening = client.SendRequestAsync<SubscriptionsListenRequestParams, EmptyResult>(
            RequestMethods.SubscriptionsListen,
            new SubscriptionsListenRequestParams { Notifications = new SubscriptionsListenNotifications { ResourceSubscriptions = ["datum://events"] } },
            cancellationToken: stop.Token);
        await Task.Delay(1000);

        await client.CallToolAsync("doc_new", new Dictionary<string, object?> { ["type"] = "part" });
        var notice = await heard.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await client.CallToolAsync("doc_close", new Dictionary<string, object?> { ["discardChanges"] = true });
        await stop.CancelAsync();
        _ = await Task.WhenAny(listening.AsTask(), Task.Delay(2000));

        Assert.Contains("datum://events", notice, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FilletTheseFilletsTheSelectedEdges()
    {
        var options = new StdioClientTransportOptions { Name = "datum", Command = DatumExecutable(), Arguments = ["--allow", Path.GetTempPath()] };
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(options));
        static Dictionary<string, object?> Args(params (string Key, object? Value)[] args) => args.ToDictionary(a => a.Key, a => a.Value);
        await client.CallToolAsync("toolset_enable", Args(("name", "features")));
        await client.CallToolAsync("doc_new", Args(("type", "part")));
        try
        {
            await client.CallToolAsync("sketch_begin", Args(("on", "Front")));
            await client.CallToolAsync("sketch_add", Args(("entities", new object[] { new Dictionary<string, object?> { ["type"] = "center_rect", ["cx"] = 0, ["cy"] = 0, ["width"] = 40, ["height"] = 20 } })));
            var sketch = TextOf(await client.CallToolAsync("sketch_end"));
            await client.CallToolAsync("feature_extrude", Args(("sketch", System.Text.RegularExpressions.Regex.Match(sketch, @"feat#\d+").Value), ("depth", 10)));
            var edges = TextOf(await client.CallToolAsync("model_faces", Args(("kind", "edge"), ("direction", "z"), ("limit", 2)))).Split('\n').Where(l => l.StartsWith("edge#", StringComparison.Ordinal)).Select(l => l.Split(' ')[0]).ToArray();
            await client.CallToolAsync("model_select", Args(("handles", edges)));

            var fillet = TextOf(await client.CallToolAsync("feature_fillet", Args(("edges", SelectedEdges), ("radius", 2))));
            Console.WriteLine(fillet);

            Assert.Contains("Fillet", fillet, StringComparison.Ordinal);
            Assert.Contains("(-", fillet, StringComparison.Ordinal);
        }
        finally
        {
            await client.CallToolAsync("doc_close", Args(("discardChanges", true)));
        }
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
