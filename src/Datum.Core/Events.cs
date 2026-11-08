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
    private readonly Queue<SolidWorksEvent> _events = new();

    public event Action<SolidWorksEvent>? Added;

    public void Add(SolidWorksEvent happened)
    {
        lock (_gate)
        {
            _events.Enqueue(happened);
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
            return [.. _events.Where(e => kind is null || string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase)).TakeLast(count)];
        }
    }
}
