using System.Collections.Concurrent;
using System.ComponentModel;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerResourceType]
public static class LiveResources
{
    public const string EventsUri = "datum://events";
    public const string SelectionUri = "datum://selection";

    [McpServerResource(UriTemplate = EventsUri, Name = "events", MimeType = "text/plain")]
    [Description("The last 50 things SOLIDWORKS reported while the user worked. Subscribe to hear each new one.")]
    public static string Events(SolidWorksConnection solidWorks)
    {
        var events = solidWorks.Events.Recent(50);
        return events.Count == 0 ? "Nothing yet\n" : string.Concat(events.Select(e => $"{e}\n"));
    }

    [McpServerResource(UriTemplate = SelectionUri, Name = "selection", MimeType = "text/plain")]
    [Description("What the user has selected in the active SOLIDWORKS document, as handles other tools accept. Subscribe to hear when it changes.")]
    public static async Task<string> Selection(SolidWorksConnection solidWorks, CancellationToken cancellationToken) =>
        string.Concat((await InspectionTools.Selection(solidWorks, cancellationToken: cancellationToken).ConfigureAwait(false)).Content.OfType<TextContentBlock>().Select(t => t.Text));
}

// Which clients asked to hear about which resources. Clients on the 2026-07-28 protocol listen with subscriptions/listen,
// which the SDK keeps open but doesn't filter on stdio, so every such session gets updates for both resources. Older clients subscribe per
// resource. A notification that can't be sent is dropped: the client has gone.
public sealed class ResourceSubscriptions
{
    private readonly ConcurrentDictionary<(McpServer Server, string Uri), byte> _subscribed = new();
    // The first protocol revision with subscriptions/listen; the SDK keeps its own constant internal.
    private const string ListenProtocol = "2026-07-28";

    private readonly ConcurrentDictionary<McpServer, byte> _listening = new();

    public void Seen(McpServer server)
    {
        if (server.NegotiatedProtocolVersion is { } version && string.CompareOrdinal(version, ListenProtocol) >= 0)
        {
            _listening[server] = 0;
        }
    }

    public void Add(McpServer server, string uri) => _subscribed[(server, uri)] = 0;

    public void Remove(McpServer server, string uri) => _subscribed.TryRemove((server, uri), out _);

    public void Notify(SolidWorksEvent happened)
    {
        bool Changed(string uri) => uri == LiveResources.EventsUri || (uri == LiveResources.SelectionUri && happened.Kind is "selection" or "active");
        var targets = _subscribed.Keys.Concat(_listening.Keys.SelectMany(server => new[] { (server, LiveResources.EventsUri), (server, LiveResources.SelectionUri) }));
        foreach (var (server, uri) in targets.Where(target => Changed(target.Item2)).Distinct())
        {
            _ = SendAsync(server, uri);
        }
    }

    private async Task SendAsync(McpServer server, string uri)
    {
        try
        {
            await server.SendNotificationAsync(NotificationMethods.ResourceUpdatedNotification, new ResourceUpdatedNotificationParams { Uri = uri }).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            Remove(server, uri);
            _listening.TryRemove(server, out _);
        }
    }
}
