using System.IO.Pipelines;
using Datum.Fakes;
using Datum.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class ReadOnlyModeTests : IAsyncLifetime
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private IHost? _host;
    private McpClient? _client;

    public async Task InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDatum(() => new FakeBackend(), new DatumOptions { AllTools = true, ReadOnly = true })
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

    [Fact]
    public async Task OnlyToolsThatCantChangeAModelAreListed()
    {
        var names = (await _client!.ListToolsAsync()).Select(t => t.Name).ToHashSet();

        Assert.Contains("model_info", names);
        Assert.Contains("model_make_check", names);
        Assert.Contains("doc_open", names);
        Assert.DoesNotContain("feature_extrude", names);
        Assert.DoesNotContain("doc_save", names);
        Assert.DoesNotContain("run_macro", names);
    }

    [Fact]
    public async Task AWriteToolIsRefusedWithAHint()
    {
        var reply = await _client!.CallToolAsync("doc_new", new Dictionary<string, object?> { ["type"] = "part" });

        Assert.True(reply.IsError);
        Assert.StartsWith("NOT_ALLOWED: doc_new can change a model and Datum runs read-only. Hint: Restart Datum without --read-only", StatusToolsTests.TextOf(reply), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindToolsSaysWhyAWriteToolIsMissing()
    {
        var reply = StatusToolsTests.TextOf(await _client!.CallToolAsync("find_tools", new Dictionary<string, object?> { ["query"] = "extrude" }));

        Assert.Contains("Datum runs read-only", reply, StringComparison.Ordinal);
    }
}
