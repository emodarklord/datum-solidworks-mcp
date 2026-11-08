namespace Datum.Core;

// Kind is active, open, new, rebuild, selection, save or close. Document is the title SOLIDWORKS shows.
public sealed record SolidWorksEvent(DateTimeOffset At, string Kind, string Document, string? Detail)
{
    public override string ToString() => $"{At.LocalDateTime:HH:mm:ss} {Kind} {Document}{(Detail is null ? "" : $": {Detail}")}";
}

// What SOLIDWORKS reported while the user worked, newest last. Events arrive on the STA thread; readers are elsewhere.
public sealed class EventLog
{
    private const int Kept = 200;

    private readonly Lock _gate = new();
    private readonly Queue<(long Number, SolidWorksEvent Event)> _events = new();
    private long _count;

    public event Action<SolidWorksEvent>? Added;

    // How many events have ever been added, so a caller can ask for what came after a point.
    public long Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    public void Add(SolidWorksEvent happened)
    {
        lock (_gate)
        {
            _events.Enqueue((++_count, happened));
            while (_events.Count > Kept)
            {
                _events.Dequeue();
            }
        }

        Added?.Invoke(happened);
    }

    public IReadOnlyList<SolidWorksEvent> Recent(int count, string? kind = null)
    {
        lock (_gate)
        {
            return [.. _events.Select(e => e.Event).Where(e => kind is null || string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)).TakeLast(count)];
        }
    }

    public IReadOnlyList<SolidWorksEvent> Between(long after, long upTo)
    {
        lock (_gate)
        {
            return [.. _events.Where(e => e.Number > after && e.Number <= upTo).Select(e => e.Event)];
        }
    }
}
