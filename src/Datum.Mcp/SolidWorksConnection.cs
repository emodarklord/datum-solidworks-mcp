using Datum.Core;

namespace Datum.Mcp;

// Connects on first use and again after SOLIDWORKS goes away, so the server can start
// before SOLIDWORKS does and survive it being restarted.
public sealed class SolidWorksConnection(Func<ISolidWorksBackend> backendFactory, TimeSpan callTimeout) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SolidWorksClient? _client;

    // Datum's own undo journal; document ids are new after a reconnect, so it lives with the connection.
    public Checkpoints Checkpoints { get; } = new();

    // Trial changes in progress, by document id.
    public Dictionary<string, Trial> Trials { get; } = [];

    // Every modelling step of this session, in CAD-neutral form.
    public IntentLog Intent { get; } = new();

    // What SOLIDWORKS reported while the user worked; it carries on across reconnects.
    public EventLog Events { get; } = new();

    // How far through Events the model has been told about broken rebuilds.
    public long ReportedEvents { get; set; }

    // null when SOLIDWORKS goes away.
    public event Action<SolidWorksCapabilities?>? CapabilitiesChanged;

    public async Task<T> UseAsync<T>(Func<SolidWorksClient, Task<T>> call, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await call(client).ConfigureAwait(false);
        }
        catch (DatumException e) when (e.Code == ErrorCode.NotRunning)
        {
            await DropAsync(client).ConfigureAwait(false);
            throw;
        }
    }

    public void Refresh(SolidWorksInfo info) => CapabilitiesChanged?.Invoke(SolidWorksCapabilities.From(info));

    public void Dispose()
    {
        _client?.Dispose();
        _gate.Dispose();
    }

    private async Task<SolidWorksClient> GetClientAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_client is not null)
            {
                return _client;
            }

            var client = await SolidWorksClient.ConnectAsync(backendFactory(), launch: false, callTimeout, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var info = await client.ReadInfoAsync(cancellationToken).ConfigureAwait(false);
                await client.RunAsync(b => { b.Subscribe(Events.Add); return true; }, cancellationToken: cancellationToken).ConfigureAwait(false);
                _client = client;
                Refresh(info);
                return client;
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DropAsync(SolidWorksClient client)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_client, client))
            {
                _client = null;
                client.Dispose();
                CapabilitiesChanged?.Invoke(null);
            }
        }
        finally
        {
            _gate.Release();
        }
    }
}
