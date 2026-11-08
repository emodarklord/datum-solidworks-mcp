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

public sealed class McpProtocolTests : IAsyncLifetime, IDisposable
{
    private readonly Pipe _toServer = new();
    private readonly Pipe _toClient = new();
    private readonly FakeBackend _backend = new();
    private IHost? _host;
    private McpClient? _client;

    public async Task InitializeAsync()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddDatum(() => _backend).WithStreamServerTransport(_toServer.Reader.AsStream(), _toClient.Writer.AsStream());
        _host = builder.Build();
        await _host.StartAsync();

        // Pinned below 2026-07-28: from that revision on, clients only get list_changed after
        // subscribing through subscriptions/listen, which the SDK client doesn't do by itself.
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

    public void Dispose() => _backend.Dispose();

    [Fact]
    public async Task ListsSwStatusAsReadOnly()
    {
        var tools = await _client!.ListToolsAsync();

        Assert.Equal(
            ["checkpoint_list", "checkpoint_restore", "doc_activate", "doc_allow_edit", "doc_close", "doc_export", "doc_list", "doc_new", "doc_open", "doc_save", "feature_extrude", "find_tools", "model_faces", "model_info", "model_select", "model_tree", "selection_get", "sketch_add", "sketch_begin", "sketch_constrain", "sketch_end", "sw_status", "toolset_enable", "view_capture"],
            tools.Select(t => t.Name).Order(StringComparer.Ordinal));
        Assert.True(tools.Single(t => t.Name == "doc_save").ProtocolTool.Annotations?.DestructiveHint);
        Assert.True(tools.Single(t => t.Name == "sw_status").ProtocolTool.Annotations?.ReadOnlyHint);
        Assert.Equal("datum", _client!.ServerInfo?.Name);
    }

    [Fact]
    public async Task CallsSwStatus()
    {
        var result = await _client!.CallToolAsync("sw_status");

        Assert.NotEqual(true, result.IsError);
        Assert.StartsWith(DiagnoseReport.Header(_backend.Info), StatusToolsTests.TextOf(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnablingAToolsetNotifiesTheClient()
    {
        var listChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var registration = _client!.RegisterNotificationHandler(
            NotificationMethods.ToolListChangedNotification,
            (_, _) =>
            {
                listChanged.TrySetResult();
                return ValueTask.CompletedTask;
            });

        var result = await _client.CallToolAsync("toolset_enable", new Dictionary<string, object?> { ["name"] = "diagnostics" });
        await listChanged.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var tools = await _client.ListToolsAsync();

        Assert.Equal("Enabled diagnostics: sw_addins\n", StatusToolsTests.TextOf(result));
        Assert.Contains(tools, t => t.Name == "sw_addins");
    }

    [Fact]
    public async Task FindsToolsInToolsetsThatAreNotLoaded()
    {
        var result = await _client!.CallToolAsync("find_tools", new Dictionary<string, object?> { ["query"] = "add-in guid" });

        Assert.Equal(
            "sw_addins [diagnostics]: List every registered SOLIDWORKS add-in with its GUID and whether it is loaded right now. "
                + "Names are unreliable; use GUIDs to identify add-ins. (enable toolset 'diagnostics')\n",
            StatusToolsTests.TextOf(result));
    }

    [Fact]
    public async Task ListsToolsetsWithoutAQuery()
    {
        var result = await _client!.CallToolAsync("find_tools");

        Assert.Equal(
            "core (24): sw_status, toolset_enable, find_tools, doc_list, doc_new, doc_open, doc_save, doc_export, doc_activate, doc_close, doc_allow_edit, "
                + "model_tree, model_info, model_faces, model_select, selection_get, sketch_begin, sketch_add, sketch_constrain, sketch_end, feature_extrude, "
                + "checkpoint_list, checkpoint_restore, view_capture\n"
                + "api (4): api_search, run_macro, api_members, invoke\n"
                + "assembly (13): component_add, component_list, component_fix, component_move, mate_add, mate_list, assembly_check, assembly_bom, config_list, config_add, config_activate, explode_create, explode_show\n"
                + "check (6): model_measure, sketch_read, model_summary, model_flex, requirements_set, requirements_check\n"
                + "diagnostics (1): sw_addins\n"
                + "drawing (31): drawing_new, view_add, view_list, view_set, sheet_layout, view_delete, sheet_set, view_entities, view_section, view_detail, view_auxiliary, dimensions_import, dimension_list, dimension_add, dimensions_auto, dimensions_tidy, dimension_tolerance, marks_add, note_add, note_edit, annotation_list, annotation_move, annotation_delete, drawing_check, title_set, bom_add, bom_read, balloons_add, balloons_arrange, bom_move, explode_lines\n"
                + "edit (14): sketch_edit, try_start, try_diff, try_end, checkpoint_create, model_dimensions, dimension_set, feature_suppress, feature_delete, rebuild, material_set, appearance_set, property_list, property_set\n"
                + "features (12): feature_revolve, feature_sweep, feature_rib, feature_loft, feature_fillet, feature_chamfer, feature_hole, feature_pattern, plane_create, axis_create, feature_mirror, feature_shell\n"
                + "intent (1): intent_export\n"
                + "live (1): events_recent\n"
                + "motion (6): motion_new, motion_motor, motion_path, motion_run, motion_export, motion_turntable\n" + "sheetmetal (5): sheet_base, sheet_bend, sheet_hem, sheet_flatten, sheet_export_flat\n"
                + "weldment (4): weldment_profiles, weldment_member, weldment_trim, cut_list\n",
            StatusToolsTests.TextOf(result));
    }

    [Fact]
    public async Task UnknownToolsetIsAnInvalidArgument()
    {
        var result = await _client!.CallToolAsync("toolset_enable", new Dictionary<string, object?> { ["name"] = "nope" });

        Assert.True(result.IsError);
        Assert.Equal("INVALID_ARGUMENT: no toolset 'nope'. Hint: known toolsets: api, assembly, check, diagnostics, drawing, edit, features, intent, live, motion, sheetmetal, weldment", StatusToolsTests.TextOf(result));
    }

    [Fact]
    public async Task ReturnsErrorsWithTheirCode()
    {
        _backend.ConnectFailure = ErrorCode.NotRunning;

        var result = await _client!.CallToolAsync("sw_status");

        Assert.True(result.IsError);
        Assert.StartsWith("NOT_RUNNING: ", Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text, StringComparison.Ordinal);
    }
}
