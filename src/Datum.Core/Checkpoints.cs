using System.Globalization;

namespace Datum.Core;

// How to undo one of Datum's own writes. SOLIDWORKS's undo stack also holds the user's GUI
// actions and splits API calls into several entries, so Datum keeps its own record instead.
public abstract record Reversal;

public sealed record DeleteCreated(IReadOnlyList<string> Features) : Reversal;

public sealed record RestoreDimension(string Name, double Value) : Reversal;

public sealed record RestoreSuppression(IReadOnlyList<string> Features, bool Suppressed) : Reversal;

// A step Datum can't undo (a deletion, or a gateway call), so a restore may not cross it. Why finishes
// the sentence "Datum can't undo …".
public sealed record NoReversal(string Why) : Reversal;

public sealed record RemoveComponent(string Handle) : Reversal;

public sealed record RestorePose(string Component, ComponentPose Pose) : Reversal;

public sealed record RemoveMate(string Feature, IReadOnlyList<ComponentMoved> Moved) : Reversal;

public sealed record RestoreFixed(string Component, bool Fixed) : Reversal;

public sealed record RestoreFlexible(string Component, bool Flexible) : Reversal;

public sealed record RestoreMaterial(string? Name, string? Database) : Reversal;

// OldValue null means the property did not exist, so restoring removes it.
public sealed record RestoreProperty(string Name, string? OldValue, string Configuration) : Reversal;

public sealed record CheckpointEntry(string Id, string Label, Reversal? Reversal);

public sealed record Trial(string Checkpoint, ModelInfo Before);

public sealed class Checkpoints
{
    private static int _next;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, List<CheckpointEntry>> _journals = [];

    // The checkpoint sits just before the step, so restoring it undoes the step too.
    public string Record(string documentId, string label, Reversal reversal) => Add(documentId, label, reversal);

    public string Mark(string documentId, string label) => Add(documentId, label, null);

    public IReadOnlyList<CheckpointEntry> List(string documentId)
    {
        lock (_lock)
        {
            return _journals.TryGetValue(documentId, out var journal) ? [.. Enumerable.Reverse(journal)] : [];
        }
    }

    // Newest first. Throws instead of returning a partial list, so a blocked restore changes nothing.
    public IReadOnlyList<CheckpointEntry> StepsToUndo(string documentId, string checkpointId)
    {
        lock (_lock)
        {
            var journal = _journals.GetValueOrDefault(documentId) ?? [];
            var index = journal.FindIndex(e => string.Equals(e.Id, checkpointId, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                throw new DatumException(ErrorCode.NotFound, $"no checkpoint '{checkpointId}' for {documentId}", "Call checkpoint_list");
            }

            var steps = journal.Skip(index).Where(e => e.Reversal is not null).Reverse().ToList();
            if (steps.FirstOrDefault(e => e.Reversal is NoReversal) is { Reversal: NoReversal blocker } barrier)
            {
                throw new DatumException(
                    ErrorCode.CannotRestore,
                    $"'{barrier.Label}' happened after {checkpointId}, and Datum can't undo {blocker.Why}",
                    "Restore a later checkpoint; while experimenting, suppress instead of deleting and prefer curated tools to invoke");
            }

            return steps;
        }
    }

    // Drops the undone steps; a named checkpoint stays so it can be restored to again.
    public void Truncate(string documentId, string checkpointId)
    {
        lock (_lock)
        {
            if (_journals.TryGetValue(documentId, out var journal))
            {
                var index = journal.FindIndex(e => string.Equals(e.Id, checkpointId, StringComparison.OrdinalIgnoreCase));
                if (index >= 0)
                {
                    var keep = journal[index].Reversal is null ? index + 1 : index;
                    journal.RemoveRange(keep, journal.Count - keep);
                }
            }
        }
    }

    public void Forget(string documentId)
    {
        lock (_lock)
        {
            _journals.Remove(documentId);
        }
    }

    private string Add(string documentId, string label, Reversal? reversal)
    {
        var id = string.Create(CultureInfo.InvariantCulture, $"cp{Interlocked.Increment(ref _next)}");
        lock (_lock)
        {
            if (!_journals.TryGetValue(documentId, out var journal))
            {
                _journals[documentId] = journal = [];
            }

            journal.Add(new CheckpointEntry(id, label, reversal));
        }

        return id;
    }
}
