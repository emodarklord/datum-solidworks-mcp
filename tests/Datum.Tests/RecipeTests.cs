using System.IO.Pipelines;
using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class RecipeTests : IAsyncLifetime, IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-recipes-").FullName;
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private readonly FakeBackend _backend = new();
    private IHost? _host;
    private McpClient? _client;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Datum"));
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDatum(() => _backend, new DatumOptions { AllTools = true, Workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups")) })
            .WithStreamServerTransport(_toServer.Reader.AsStream(), _toClient.Writer.AsStream());
        _host = builder.Build();
        await _host.StartAsync();
        _client = await McpClient.CreateAsync(
            new StreamClientTransport(_toServer.Writer.AsStream(), _toClient.Reader.AsStream()),
            new McpClientOptions { ProtocolVersion = "2025-11-25" });
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task ASavedSessionIsListedAndReplaysIntoANewPart()
    {
        await Call("doc_new", new() { ["type"] = "part" });
        await Call("sketch_begin", new() { ["on"] = "Front" });
        await Call("sketch_add", new() { ["entities"] = new[] { new Dictionary<string, object> { ["type"] = "circle", ["cx"] = 0, ["cy"] = 0, ["radius"] = 10 } } });
        var sketch = System.Text.RegularExpressions.Regex.Match(await Call("sketch_end", []), @"feat#\d+").Value;
        await Call("feature_extrude", new() { ["sketch"] = sketch, ["depth"] = 5 });
        await Call("intent_export", new() { ["path"] = "puck.intent.json" });

        var listed = await Read(RecipeResources.ListUri);
        var steps = await Read(RecipeResources.ListUri + "/puck");
        var ran = await Call("recipe_run", new() { ["name"] = "puck" });

        Assert.Equal("puck: 5 steps (doc_new, sketch_begin, sketch_add, sketch_end, feature_extrude)\n", listed);
        Assert.StartsWith("puck: 5 steps", steps, StringComparison.Ordinal);
        Assert.Contains("5. feature_extrude {", steps, StringComparison.Ordinal);
        Assert.StartsWith("Ran puck: 5 steps\nLast step: ", ran, StringComparison.Ordinal);
        Assert.Equal(2, _backend.ListDocuments().Count);
    }

    [Fact]
    public async Task AMissingRecipeSaysHowToMakeOne()
    {
        var reply = await _client!.CallToolAsync("recipe_run", new Dictionary<string, object?> { ["name"] = "nope" });

        Assert.True(reply.IsError);
        Assert.StartsWith("NOT_FOUND: no recipe 'nope'", StatusToolsTests.TextOf(reply), StringComparison.Ordinal);
        Assert.StartsWith("No recipes yet", await Read(RecipeResources.ListUri), StringComparison.Ordinal);
    }

    private async Task<string> Call(string tool, Dictionary<string, object?> args)
    {
        var reply = await _client!.CallToolAsync(tool, args);
        Assert.True(reply.IsError != true, StatusToolsTests.TextOf(reply));
        return StatusToolsTests.TextOf(reply);
    }

    private async Task<string> Read(string uri) =>
        string.Concat((await _client!.ReadResourceAsync(uri)).Contents.OfType<TextResourceContents>().Select(c => c.Text));
}
