using Datum.Core;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

public sealed record DatumOptions
{
    // Lists every tool up front and never changes the list, for clients that mishandle
    // notifications/tools/list_changed.
    public bool AllTools { get; init; }

    public Workspace? Workspace { get; init; }

    // Lets invoke make calls that may change a model; reads work either way.
    public bool AllowInvokeWrites { get; init; }

    // Lets run_macro run. A macro can do anything the user can, and Datum can't undo it.
    public bool AllowMacros { get; init; }
}

public static class DatumServer
{
    private static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    public static IMcpServerBuilder AddDatum(this IServiceCollection services, Func<ISolidWorksBackend> backendFactory, DatumOptions? options = null)
    {
        options ??= new DatumOptions();
        services.AddSingleton(options);
        services.AddSingleton(options.Workspace ?? Workspace.Create([], Environment.GetEnvironmentVariable("DATUM_ALLOW")));
        services.AddSingleton<ResourceSubscriptions>();
        services.AddSingleton(provider =>
        {
            var connection = new SolidWorksConnection(backendFactory, CallTimeout);
            connection.Events.Added += provider.GetRequiredService<ResourceSubscriptions>().Notify;
            return connection;
        });
        services.AddSingleton(provider =>
        {
            var catalog = new ToolCatalog(options.AllTools);
            provider.GetRequiredService<SolidWorksConnection>().CapabilitiesChanged += catalog.Update;
            return catalog;
        });
        services.AddOptions<McpServerOptions>()
            .PostConfigure<ToolCatalog>((serverOptions, catalog) => catalog.Attach(serverOptions.ToolCollection ??= []));

        return services
            .AddMcpServer(serverOptions => serverOptions.ServerInfo = new Implementation
            {
                Name = "datum",
                Version = typeof(DatumServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
            })
            .WithResources([typeof(LiveResources)])
            .WithSubscribeToResourcesHandler((request, _) =>
            {
                request.Services?.GetRequiredService<ResourceSubscriptions>().Add(request.Server, request.Params?.Uri ?? "");
                return ValueTask.FromResult(new EmptyResult());
            })
            .WithUnsubscribeFromResourcesHandler((request, _) =>
            {
                request.Services?.GetRequiredService<ResourceSubscriptions>().Remove(request.Server, request.Params?.Uri ?? "");
                return ValueTask.FromResult(new EmptyResult());
            })
            .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
            {
                var tool = context.Params?.Name ?? "";
                context.Services?.GetRequiredService<ResourceSubscriptions>().Seen(context.Server);
                var connection = context.Services?.GetRequiredService<SolidWorksConnection>();
                var loose = IntentLog.Records(tool) ? IntentLog.LooseHandles(System.Text.Json.JsonSerializer.Serialize(context.Params?.Arguments)) : [];
                var refs = connection is not null && loose.Count > 0 ? await InspectionTools.DescribeAsync(connection, loose, cancellationToken).ConfigureAwait(false) : [];
                var reply = await next(context, cancellationToken).ConfigureAwait(false);
                if (reply.IsError != true && IntentLog.Records(tool))
                {
                    var text = string.Concat(reply.Content.OfType<TextContentBlock>().Select(block => block.Text));
                    connection?.Intent.Add(tool, context.Params?.Arguments?.ToDictionary(), text, refs);
                }

                return reply;
            }))
            .WithTools([typeof(StatusTools), typeof(ToolsetTools), typeof(DocumentTools), typeof(InspectionTools), typeof(SketchTools), typeof(FeatureTools), typeof(EditTools), typeof(ViewTools), typeof(CheckTools), typeof(AssemblyTools), typeof(DrawingTools), typeof(MotionTools), typeof(WeldmentTools), typeof(SheetMetalTools), typeof(IntentTools), typeof(LiveTools), typeof(ApiTools)]);
    }
}
