using Datum.Core;
using SolidWorks.Interop.sldworks;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    // The handlers are only referenced from COM, so they're kept here or they'd be collected and stop firing.
    private readonly List<(IModelDoc2 Document, Action Unhook)> _hooked = [];
    private Action<SolidWorksEvent>? _sink;

    public void Subscribe(Action<SolidWorksEvent> sink) => Call(() =>
    {
        _sink = sink;
        var app = (DSldWorksEvents_Event)App;
        app.ActiveDocChangeNotify += () => Report("active", App.IActiveDoc2, null);
        app.FileOpenPostNotify += path =>
        {
            var opened = ((object[]?)App.GetDocuments() ?? []).OfType<IModelDoc2>().FirstOrDefault(d => string.Equals(d.GetPathName(), path, StringComparison.OrdinalIgnoreCase));
            Hook(opened);
            return Report("open", opened, null);
        };
        app.FileNewNotify2 += (created, _, _) =>
        {
            Hook(created as IModelDoc2);
            return Report("new", created as IModelDoc2, null);
        };
        foreach (var document in ((object[]?)App.GetDocuments() ?? []).OfType<IModelDoc2>().Where(d => d.Visible))
        {
            Hook(document);
        }

        return true;
    });

    private void Hook(IModelDoc2? document)
    {
        if (document is null || _hooked.Any(entry => ReferenceEquals(entry.Document, document)))
        {
            return;
        }

        int Rebuilt() => Report("rebuild", document, RebuildErrors(document) is { Count: > 0 } broken ? $"errors in {string.Join(", ", broken)}" : "ok");
        int Selected() => Report("selection", document, $"{((ISelectionMgr)document.SelectionManager).GetSelectedObjectCount2(-1)} selected");
        int Saved(int type, string path) => Report("save", document, path);
        int Closed(int type) => Unhook(document);
        switch (document)
        {
            case PartDoc part:
                var partEvents = (DPartDocEvents_Event)part;
                DPartDocEvents_RegenPostNotify2EventHandler partRebuilt = _ => Rebuilt();
                DPartDocEvents_UserSelectionPostNotifyEventHandler partSelected = Selected;
                DPartDocEvents_FileSavePostNotifyEventHandler partSaved = Saved;
                DPartDocEvents_DestroyNotify2EventHandler partClosed = Closed;
                partEvents.RegenPostNotify2 += partRebuilt;
                partEvents.UserSelectionPostNotify += partSelected;
                partEvents.FileSavePostNotify += partSaved;
                partEvents.DestroyNotify2 += partClosed;
                _hooked.Add((document, () =>
                {
                    partEvents.RegenPostNotify2 -= partRebuilt;
                    partEvents.UserSelectionPostNotify -= partSelected;
                    partEvents.FileSavePostNotify -= partSaved;
                    partEvents.DestroyNotify2 -= partClosed;
                }
                ));
                break;

            case AssemblyDoc assembly:
                var assemblyEvents = (DAssemblyDocEvents_Event)assembly;
                DAssemblyDocEvents_RegenPostNotify2EventHandler assemblyRebuilt = _ => Rebuilt();
                DAssemblyDocEvents_UserSelectionPostNotifyEventHandler assemblySelected = Selected;
                DAssemblyDocEvents_FileSavePostNotifyEventHandler assemblySaved = Saved;
                DAssemblyDocEvents_DestroyNotify2EventHandler assemblyClosed = Closed;
                assemblyEvents.RegenPostNotify2 += assemblyRebuilt;
                assemblyEvents.UserSelectionPostNotify += assemblySelected;
                assemblyEvents.FileSavePostNotify += assemblySaved;
                assemblyEvents.DestroyNotify2 += assemblyClosed;
                _hooked.Add((document, () =>
                {
                    assemblyEvents.RegenPostNotify2 -= assemblyRebuilt;
                    assemblyEvents.UserSelectionPostNotify -= assemblySelected;
                    assemblyEvents.FileSavePostNotify -= assemblySaved;
                    assemblyEvents.DestroyNotify2 -= assemblyClosed;
                }
                ));
                break;

            case DrawingDoc drawing:
                var drawingEvents = (DDrawingDocEvents_Event)drawing;
                DDrawingDocEvents_RegenPostNotifyEventHandler drawingRebuilt = Rebuilt;
                DDrawingDocEvents_UserSelectionPostNotifyEventHandler drawingSelected = Selected;
                DDrawingDocEvents_FileSavePostNotifyEventHandler drawingSaved = Saved;
                DDrawingDocEvents_DestroyNotify2EventHandler drawingClosed = Closed;
                drawingEvents.RegenPostNotify += drawingRebuilt;
                drawingEvents.UserSelectionPostNotify += drawingSelected;
                drawingEvents.FileSavePostNotify += drawingSaved;
                drawingEvents.DestroyNotify2 += drawingClosed;
                _hooked.Add((document, () =>
                {
                    drawingEvents.RegenPostNotify -= drawingRebuilt;
                    drawingEvents.UserSelectionPostNotify -= drawingSelected;
                    drawingEvents.FileSavePostNotify -= drawingSaved;
                    drawingEvents.DestroyNotify2 -= drawingClosed;
                }
                ));
                break;
        }
    }

    private int Unhook(IModelDoc2 document)
    {
        Report("close", document, null);
        foreach (var entry in _hooked.Where(entry => ReferenceEquals(entry.Document, document)).ToList())
        {
            entry.Unhook();
            _hooked.Remove(entry);
        }

        return 0;
    }

    // Handlers must not throw back into SOLIDWORKS, and they return 0 to let it carry on.
    private int Report(string kind, IModelDoc2? document, string? detail)
    {
        try
        {
            _sink?.Invoke(new SolidWorksEvent(DateTimeOffset.Now, kind, document?.GetTitle() ?? "", detail));
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            // A document going away mid-event has nothing left to report.
        }

        return 0;
    }
}
