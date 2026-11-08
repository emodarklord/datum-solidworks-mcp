using System.Globalization;
using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    private const int ImportFromEntireModel = 0;
    private const double AxisTolerance = 1e-3;
    private const double MarkPositionTolerance = 1e-4;
    private const double SurfaceTolerance = 1e-5;
    private const short VisibleLines = 1;
    private const int LineRecordLength = 12;
    private const int LineStart = 6;
    private const int LineEnd = 9;
    private const double LineThickness = 0.05;
    private const double BorderTolerance = 0.5;
    private const string FormatInches = "IN INCHES";
    private const string FormatMm = "IN MM";
    private const double BalloonRadius = 6;
    private const int MaxToleranceDecimals = 4;

    // swSelEXTSKETCHPOINT: what a sketch dimension attached to the origin reports.
    private const int OriginPointType = 25;

    private readonly Dictionary<string, (string Template, HashSet<string> Names)> _formatNotes = [];

    public IReadOnlyList<ViewInfo> ImportDimensions(string id, string? view) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        document.ClearSelection2(true);
        var target = view is null ? null : ViewOf(id, document, view);
        if (target is not null)
        {
            document.Extension.SelectByID2(target.Name, "DRAWINGVIEW", 0, 0, 0, false, 0, null, 0);
        }

        drawing.InsertModelAnnotations3(ImportFromEntireModel, (int)swInsertAnnotation_e.swInsertDimensionsMarkedForDrawing, target is null, true, false, false);
        document.ClearSelection2(true);
        document.EditRebuild3();
        return (target is null ? ViewsOf(document) : [target]).Select(v => DescribeView(id, document, v)).ToList();
    });

    public IReadOnlyList<SheetDimension> ListSheetDimensions(string id, string? view) => Call(() =>
    {
        var document = Find(id);
        DrawingOf(document, id);
        var views = view is null ? ViewsOf(document) : [ViewOf(id, document, view)];
        return views.SelectMany(v => (v.GetDisplayDimensions() as object[] ?? []).OfType<IDisplayDimension>().Select(shown => DescribeSheetDimension(id, document, v, shown))).ToList();
    });

    public SheetDimension AddSheetDimension(string id, string view, string first, string? second, double x, double y) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var target = ViewOf(id, document, view);
        drawing.ActivateView(target.Name);
        document.ClearSelection2(true);
        foreach (var (handle, append) in second is null ? new[] { (first, false) } : [(first, false), (second, true)])
        {
            var edge = Resolve(id, document, handle) as IEdge
                ?? throw new DatumException(ErrorCode.InvalidArgument, $"{handle} is not an edge", "Use an edge# handle from view_entities");
            if (!target.SelectEntity(edge, append))
            {
                throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't select {handle} in {view}");
            }
        }

        var added = WithoutDimensionPromptResult(() => document.AddDimension2(x / Mm, y / Mm, 0) as IDisplayDimension);
        document.ClearSelection2(true);
        return added is null
            ? throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't dimension {first}{(second is null ? "" : $" and {second}")}")
            : DescribeSheetDimension(id, document, target, added);
    });

    public ToleranceChange SetTolerance(string id, string dimension, string kind, double upper, double lower) => Call(() =>
    {
        var document = Find(id);
        DrawingOf(document, id);
        var (view, shownOnSheet, found) = ViewsOf(document)
            .SelectMany(v => (v.GetDisplayDimensions() as object[] ?? []).OfType<IDisplayDimension>().Select(shown => (View: v, Shown: shown, Dimension: shown.GetDimension2(0))))
            .FirstOrDefault(pair => string.Equals(ShortName(pair.Dimension), dimension, StringComparison.OrdinalIgnoreCase));
        if (found is null)
        {
            throw new DatumException(ErrorCode.NotFound, $"no dimension '{dimension}' on this drawing", "Call dimension_list");
        }

        var code = Tolerances.Code(kind);
        var low = code == (int)swTolType_e.swTolSYMMETRIC ? -Math.Abs(upper) : lower;
        var tolerance = found.Tolerance;
        tolerance.Type = code;
        // From a drawing "this configuration" means nothing, so SetValues2 refuses it; the plain call applies everywhere.
        if (code != (int)swTolType_e.swTolNONE
            && !tolerance.SetValues2(low / Mm, upper / Mm, (int)swSetValueInConfiguration_e.swSetValue_InAllConfigurations, null)
            && !tolerance.SetValues(low / Mm, upper / Mm))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS refused that tolerance on {dimension}");
        }

        // The sheet prints tolerances to a fixed number of decimals, so -0.016 would show as -0.02 and state a
        // different fit.
        var decimals = Enumerable.Range(1, MaxToleranceDecimals).FirstOrDefault(places => Math.Abs((upper * Math.Pow(10, places)) - Math.Round(upper * Math.Pow(10, places))) < 1e-6 && Math.Abs((low * Math.Pow(10, places)) - Math.Round(low * Math.Pow(10, places))) < 1e-6, MaxToleranceDecimals);
        shownOnSheet.SetPrecision3(-1, -1, decimals, -1);
        document.EditRebuild3();

        // The tolerance lives on the model, but changing it from a drawing doesn't mark the model as changed, so
        // without this a save of the model would skip it.
        var reference = found.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriven;
        if (!reference)
        {
            (view.ReferencedDocument as IModelDoc2)?.SetSaveFlag();
        }

        var read = found.Tolerance;
        read.GetMinValue2(out var readLow);
        read.GetMaxValue2(out var readHigh);
        if (read.Type != code || (code != (int)swTolType_e.swTolNONE && Math.Abs(readHigh - (upper / Mm)) > 1e-9))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't keep the tolerance on {dimension}");
        }

        return new ToleranceChange(ShortName(found), kind.Trim().ToLowerInvariant(), readHigh * Mm, readLow * Mm, reference ? null : view.GetReferencedModelName());
    });

    public MarkCount AddMarks(string id, string view) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var target = ViewOf(id, document, view);
        if (target.Type == (int)swDrawingViewTypes_e.swDrawingDetailView)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"SOLIDWORKS can't add centre marks to the detail view {target.Name}", "Add them to its parent view; drawing_check doesn't ask for them here");
        }

        drawing.ActivateView(target.Name);
        foreach (var cylinder in CylindersIn(target).Where(c => c.EndOn && !HasCentreMark(target, c)))
        {
            // SOLIDWORKS only marks an edge the view really draws, and the first one may be behind something.
            foreach (var edge in (cylinder.Face.GetEdges() as object[] ?? []).OfType<IEdge>().Where(e => ((ICurve)e.GetCurve()).IsCircle()))
            {
                document.ClearSelection2(true);
                if (target.SelectEntity(edge, false))
                {
                    drawing.InsertCenterMark3((int)swCenterMarkStyle_e.swCenterMark_Single, false, false);
                }

                if (HasCentreMark(target, cylinder))
                {
                    break;
                }
            }
        }

        var lines = target.GetCenterLineCount();
        foreach (var cylinder in CylindersIn(target).Where(c => !c.EndOn).Skip(lines))
        {
            document.ClearSelection2(true);
            if (target.SelectEntity(cylinder.Face, false))
            {
                drawing.InsertCenterLine2();
            }
        }

        document.ClearSelection2(true);
        document.EditRebuild3();
        return CountMarks(id, document, target);
    });

    public string AddNote(string id, string text, double x, double y, string? view) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        if (view is not null)
        {
            drawing.ActivateView(ViewOf(id, document, view).Name);
        }
        else
        {
            drawing.ActivateSheet(((ISheet)drawing.GetCurrentSheet()).GetName());
        }

        var note = document.InsertNote(text) as INote
            ?? throw new DatumException(ErrorCode.SolidworksError, "SOLIDWORKS couldn't add the note");
        ((IAnnotation)note.GetAnnotation()).SetPosition2(x / Mm, y / Mm, 0);
        document.ClearSelection2(true);
        return note.GetText();
    });

    public DrawingFacts ReadDrawingFacts(string id) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var views = ViewsOf(document);
        var sheet = (ISheet)drawing.GetCurrentSheet();
        var properties = (double[])sheet.GetProperties2();
        var format = ReadFormat(id, drawing, sheet);
        var models = views.Select(view => view.GetReferencedModelName()).Where(path => path.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var parts = views.Select(view => view.ReferencedDocument).OfType<IPartDoc>().Cast<IModelDoc2>().GroupBy(model => model.GetPathName()).Select(group => group.First()).ToList();
        var assembly = views.Select(view => view.ReferencedDocument).OfType<IAssemblyDoc>().FirstOrDefault();
        return new DrawingFacts(
            properties[SheetIndexWidth] * Mm,
            properties[SheetIndexHeight] * Mm,
            [.. views.Select(view => FactsOf(id, document, view))],
            [.. parts.SelectMany(ModelDimensionsOf)],
            [.. views.SelectMany(v => (v.GetDisplayDimensions() as object[] ?? []).OfType<IDisplayDimension>().Select(shown => DescribeSheetDimension(id, document, v, shown)))],
            [.. models.Where(path => !File.Exists(path))],
            document.Extension.NeedsRebuild2 != (int)swModelRebuildStatus_e.swModelRebuildStatus_FullyRebuilt)
        {
            BomRows = BomRowsOf(document),
            BomBox = BomBoxOf(document),
            ActualBom = assembly is null ? null : BomOf(assembly),
            Balloons = assembly is null ? null : BalloonsOf(document),
            Frame = format.Frame,
            TitleBlock = format.TitleBlock,
            SheetNotes = [.. format.Notes.Select(note => DescribeAnnotation((IAnnotation)note.GetAnnotation(), null))],
            SheetScale = SheetScaleOf(sheet),
            Units = document.Extension.GetUserPreferenceInteger((int)swUserPreferenceIntegerValue_e.swUnitSystem, 0) == (int)swUnitSystem_e.swUnitSystem_IPS ? "inches" : "mm",
            FormatUnits = format.FormatUnits,
        };
    });

    public IReadOnlyList<SheetAnnotation> ListAnnotations(string id, string? view) => Call(() =>
    {
        var document = Find(id);
        var drawing = DrawingOf(document, id);
        var chosen = view is null ? ViewsOf(document) : [ViewOf(id, document, view)];
        var onViews = chosen.SelectMany(v => TextOn(v).Select(annotation => DescribeAnnotation(annotation, HandleOf(id, document, "view", v))));
        var onSheet = view is null ? ReadFormat(id, drawing, (ISheet)drawing.GetCurrentSheet()).Notes.Select(note => DescribeAnnotation((IAnnotation)note.GetAnnotation(), null)) : [];
        return onViews.Concat(onSheet).ToList();
    });

    public SheetAnnotation MoveAnnotation(string id, string name, double x, double y) => Call(() =>
    {
        var document = Find(id);
        var (annotation, view) = FindAnnotation(id, document, name);
        if (!annotation.SetPosition2(x / Mm, y / Mm, 0))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS didn't move {name}");
        }

        document.EditRebuild3();
        return DescribeAnnotation(annotation, view);
    });

    public string EditNote(string id, string name, string text) => Call(() =>
    {
        var document = Find(id);
        var (annotation, _) = FindAnnotation(id, document, name);
        if (annotation.GetSpecificAnnotation() is not INote note)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{name} isn't a note", "Use dimension_tolerance or annotation_move for dimensions");
        }

        note.SetText(text);
        document.EditRebuild3();
        return note.GetText();
    });

    public void DeleteAnnotation(string id, string name) => Call(() =>
    {
        var document = Find(id);
        var (annotation, _) = FindAnnotation(id, document, name);
        document.ClearSelection2(true);
        if (!annotation.Select3(false, null) || !document.Extension.DeleteSelection2((int)swDeleteSelectionOptions_e.swDelete_Absorbed))
        {
            throw new DatumException(ErrorCode.SolidworksError, $"SOLIDWORKS couldn't delete {name}");
        }

        document.ClearSelection2(true);
        document.EditRebuild3();
        return true;
    });

    private (IAnnotation Annotation, string? View) FindAnnotation(string id, IModelDoc2 document, string name)
    {
        var drawing = DrawingOf(document, id);
        foreach (var view in ViewsOf(document))
        {
            if (TextOn(view).FirstOrDefault(annotation => string.Equals(NameOf(annotation), name, StringComparison.OrdinalIgnoreCase)) is { } found)
            {
                return (found, HandleOf(id, document, "view", view));
            }
        }

        return ReadFormat(id, drawing, (ISheet)drawing.GetCurrentSheet()).Notes.Select(note => (IAnnotation)note.GetAnnotation()).FirstOrDefault(annotation => string.Equals(annotation.GetName(), name, StringComparison.OrdinalIgnoreCase)) is { } onSheet
            ? (onSheet, null)
            : throw new DatumException(ErrorCode.NotFound, $"no annotation '{name}' on this drawing", "Call annotation_list");
    }

    // Everything on a view that takes up room as text. Centre marks and lines have no position of their own.
    private static IEnumerable<IAnnotation> TextOn(IView view) =>
        (view.GetAnnotations() as object[] ?? []).OfType<IAnnotation>().Where(annotation => annotation.GetSpecificAnnotation() is IDisplayDimension or INote);

    private static string NameOf(IAnnotation annotation) =>
        annotation.GetSpecificAnnotation() is IDisplayDimension shown ? ShortName(shown.GetDimension2(0)) : annotation.GetName();

    // The sheet format has no API of its own. Its lines come back from the sheet's own view, at model size, as
    // twelve numbers each with the ends last. Its notes can only be told from the sheet's by hiding the format.
    private SheetFormatFacts ReadFormat(string id, IDrawingDoc drawing, ISheet sheet)
    {
        var view = (IView)drawing.GetFirstView();
        var properties = (double[])sheet.GetProperties2();
        var scale = properties[2] / properties[3];
        var raw = view.GetLines4(VisibleLines) as double[] ?? [];
        var lines = new List<SheetBox>();
        for (var at = 0; at + LineRecordLength <= raw.Length; at += LineRecordLength)
        {
            var (x1, y1, x2, y2) = (raw[at + LineStart] * scale * Mm, raw[at + LineStart + 1] * scale * Mm, raw[at + LineEnd] * scale * Mm, raw[at + LineEnd + 1] * scale * Mm);
            lines.Add(new SheetBox(Math.Min(x1, x2) - LineThickness, Math.Min(y1, y2) - LineThickness, Math.Max(x1, x2) + LineThickness, Math.Max(y1, y2) + LineThickness));
        }

        var all = (view.GetNotes() as object[] ?? []).OfType<INote>().ToList();
        // Hiding the format is slow on a full sheet, and its notes only change with the format, so one look will do.
        var template = sheet.GetTemplateName();
        if (!_formatNotes.TryGetValue(id, out var known) || known.Template != template)
        {
            var hidden = all.Select(note => note.GetName()).ToHashSet(StringComparer.Ordinal);
            if (sheet.SheetFormatVisible)
            {
                sheet.SheetFormatVisible = false;
                try
                {
                    hidden.ExceptWith((((IView)drawing.GetFirstView()).GetNotes() as object[] ?? []).OfType<INote>().Select(note => note.GetName()));
                }
                finally
                {
                    sheet.SheetFormatVisible = true;
                }
            }

            known = (template, hidden);
            _formatNotes[id] = known;
        }

        var inFormat = all.Where(note => known.Names.Contains(note.GetName())).ToList();
        var formatText = inFormat.Where(note => note.GetText().Length > 1 && note.GetExtent() is double[] { Length: >= 6 })
            .Select(note => { var e = (double[])note.GetExtent(); return new SheetBox(e[0] * Mm, e[1] * Mm, e[3] * Mm, e[4] * Mm); });
        var notes = all.Except(inFormat).ToList();
        if (lines.Count == 0)
        {
            return new SheetFormatFacts(null, [], notes, null);
        }

        var written = inFormat.Select(note => note.PropertyLinkedText).ToList();
        var claimed = written.Any(text => text.Contains(FormatInches, StringComparison.OrdinalIgnoreCase)) ? "inches"
            : written.Any(text => text.Contains(FormatMm, StringComparison.OrdinalIgnoreCase)) ? "mm" : null;

        var wide = lines.MaxBy(line => line.Right - line.Left);
        var tall = lines.MaxBy(line => line.Top - line.Bottom);
        var frame = new SheetBox(wide.Left, tall.Bottom, wide.Right, tall.Top);
        var inner = frame.Inset(BorderTolerance);
        return new SheetFormatFacts(frame, [.. lines.Where(inner.Overlaps), .. formatText.Where(inner.Overlaps)], notes, claimed);
    }

    private ViewFacts FactsOf(string id, IModelDoc2 document, IView view)
    {
        var marks = CountMarks(id, document, view);
        return new ViewFacts(
            DescribeView(id, document, view),
            OutlineOf(view),
            (view.GetOrientationName() ?? "").Contains("Isometric", StringComparison.OrdinalIgnoreCase),
            CylindersIn(view).Count(c => !c.EndOn),
            marks.MissingMarks,
            marks.MissingLines,
            [.. TextOn(view).Select(annotation => DescribeAnnotation(annotation, view.Name))])
        {
            Orientation = (view.GetOrientationName() ?? "").TrimStart('*').ToLowerInvariant(),
            Geometry = GeometryOf(view),
        };
    }

    private static SheetBox? GeometryOf(IView view)
    {
        var box = view.ReferencedDocument switch
        {
            IPartDoc part => part.GetPartBox(true) as double[],
            IAssemblyDoc assembly => assembly.GetBox(0) as double[],
            _ => null,
        };
        if (box is not { Length: 6 })
        {
            return null;
        }

        var corners = (from x in new[] { box[0], box[3] } from y in new[] { box[1], box[4] } from z in new[] { box[2], box[5] } select OnSheet(view, new Point3(x, y, z))).ToList();
        return new SheetBox(corners.Min(c => c.X), corners.Min(c => c.Y), corners.Max(c => c.X), corners.Max(c => c.Y));
    }

    private static SheetAnnotation DescribeAnnotation(IAnnotation annotation, string? view)
    {
        var at = annotation.GetPosition() as double[] ?? [0, 0];
        var specific = annotation.GetSpecificAnnotation();
        var text = specific switch
        {
            IDisplayDimension shown => ShortName(shown.GetDimension2(0)),
            INote note => note.GetText(),
            _ => annotation.GetName(),
        };
        return new SheetAnnotation(text, at[0] * Mm, at[1] * Mm, annotation.IsDangling(), TextBox(annotation, specific), NameOf(annotation), view)
        {
            Lines = specific is IDisplayDimension ? LinesOf(annotation) : [],
            Shows = specific switch
            {
                IDisplayDimension shown => ValueOf(shown.GetDimension2(0)),
                INote when string.IsNullOrWhiteSpace(text) => "(view label)",
                _ => null,
            },
        };
    }

    private static string ValueOf(IDimension dimension)
    {
        var angular = dimension.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
        return $"{Numbers.Format(FromSystem(dimension.SystemValue, angular))}{(angular ? "°" : " mm")}";
    }

    // A note knows its extent. A dimension's text is only known from what SOLIDWORKS drew: each string's corner and size.
    private static List<SheetSegment> LinesOf(IAnnotation annotation) =>
        annotation.GetDisplayData() is IDisplayData drawn
            ? [.. Enumerable.Range(0, drawn.GetLineCount()).Select(index => drawn.GetLineAtIndex3(index) as double[]).OfType<double[]>().Where(line => line.Length >= 6).Select(line => new SheetSegment(line[0] * Mm, line[1] * Mm, line[3] * Mm, line[4] * Mm))]
            : [];

    private static SheetBox? TextBox(IAnnotation annotation, object specific)
    {
        if (specific is INote { } balloon && balloon.IsBomBalloon() && annotation.GetPosition() is double[] { Length: >= 2 } centre)
        {
            // A balloon's extent includes its leader, which is meant to cross other things.
            return new SheetBox((centre[0] * Mm) - BalloonRadius, (centre[1] * Mm) - BalloonRadius, (centre[0] * Mm) + BalloonRadius, (centre[1] * Mm) + BalloonRadius);
        }

        if (specific is INote note)
        {
            return note.GetExtent() is double[] { Length: >= 6 } e ? new SheetBox(e[0] * Mm, e[1] * Mm, e[3] * Mm, e[4] * Mm) : null;
        }

        if (annotation.GetDisplayData() is not IDisplayData drawn || drawn.GetTextCount() == 0)
        {
            return null;
        }

        var boxes = Enumerable.Range(0, drawn.GetTextCount()).Select(index =>
        {
            var at = (double[])drawn.GetTextPositionAtIndex(index);
            return new SheetBox(at[0] * Mm, at[1] * Mm, (at[0] + drawn.GetTextInBoxWidthAtIndex(index)) * Mm, (at[1] + drawn.GetTextHeightAtIndex(index)) * Mm);
        }).ToList();
        return new SheetBox(boxes.Min(b => b.Left), boxes.Min(b => b.Bottom), boxes.Max(b => b.Right), boxes.Max(b => b.Top));
    }

    private static IEnumerable<ModelDimension> ModelDimensionsOf(IModelDoc2 model)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var owner in AllFeatures(model).Where(feature => !IsSuppressed(feature)))
        {
            for (var display = owner.GetFirstDisplayDimension(); display is not null; display = owner.GetNextDisplayDimension(display))
            {
                var dimension = ((IDisplayDimension)display).GetDimension2(0);
                if (dimension is not null && dimension.DrivenState == (int)swDimensionDrivenState_e.swDimensionDriving && seen.Add(ShortName(dimension)))
                {
                    var angular = dimension.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
                    var attached = ((IDisplayDimension)display).GetAnnotation() is IAnnotation annotation ? annotation.GetAttachedEntityTypes() as int[] ?? [] : [];
                    yield return new ModelDimension(ShortName(dimension), FromSystem(dimension.SystemValue, angular), angular ? "°" : " mm", attached.Contains(OriginPointType) || owner.GetTypeName2() == "RefPlane");
                }
            }
        }
    }

    private SheetDimension DescribeSheetDimension(string id, IModelDoc2 document, IView view, IDisplayDimension shown)
    {
        var dimension = shown.GetDimension2(0);
        var angular = dimension.GetType() == (int)swDimensionParamType_e.swDimensionParamTypeDoubleAngular;
        var annotation = (IAnnotation)shown.GetAnnotation();
        return new SheetDimension(
            HandleOf(id, document, "view", view),
            ShortName(dimension),
            FromSystem(dimension.SystemValue, angular),
            angular ? "°" : " mm",
            annotation.IsDangling(),
            ToleranceText(dimension));
    }

    private static string? ToleranceText(IDimension dimension)
    {
        var tolerance = dimension.Tolerance;
        if (tolerance is null || tolerance.Type == (int)swTolType_e.swTolNONE)
        {
            return null;
        }

        tolerance.GetMinValue2(out var low);
        tolerance.GetMaxValue2(out var high);
        var (upper, lower) = (high * Mm, low * Mm);
        return tolerance.Type == (int)swTolType_e.swTolSYMMETRIC
            ? string.Create(CultureInfo.InvariantCulture, $"±{Numbers.Format(upper)}")
            : string.Create(CultureInfo.InvariantCulture, $"+{Numbers.Format(upper)} {Numbers.Format(lower)}");
    }

    private T? WithoutDimensionPromptResult<T>(Func<T?> work)
        where T : class
    {
        T? result = null;
        WithoutDimensionPrompt(() => result = work());
        return result;
    }

    private MarkCount CountMarks(string id, IModelDoc2 document, IView view)
    {
        var cylinders = CylindersIn(view);
        var missingMarks = cylinders.Count(c => c.EndOn && !HasCentreMark(view, c));
        var lines = view.GetCenterLineCount();
        return new MarkCount(HandleOf(id, document, "view", view), CentreMarksOf(view).Count, lines, missingMarks, Math.Max(0, cylinders.Count(c => !c.EndOn) - lines));
    }

    // The full cylinders of a part's view, one per axis. Rounds are left out: their edges cover less than a circle.
    // Assembly views return none, because which holes need axes there depends on the design.
    private static List<ViewCylinder> CylindersIn(IView view)
    {
        if (view.ReferencedDocument is not IPartDoc part)
        {
            return [];
        }

        var transform = (double[])view.ModelToViewTransform.ArrayData;
        var towardViewer = new Point3(transform[2], transform[5], transform[8]);
        Point3 Flat(Point3 p) => new((p.X * transform[0]) + (p.Y * transform[3]) + (p.Z * transform[6]), (p.X * transform[1]) + (p.Y * transform[4]) + (p.Z * transform[7]), 0);
        var found = new List<ViewCylinder>();
        foreach (var face in SolidBodies(part).SelectMany(b => (b.GetFaces() as object[] ?? []).OfType<IFace2>()))
        {
            var surface = (ISurface)face.GetSurface();
            if (!surface.IsCylinder() || surface.CylinderParams is not double[] { Length: >= 7 } c)
            {
                continue;
            }

            var axis = new Point3(c[3], c[4], c[5]).Normalized();
            var origin = new Point3(c[0], c[1], c[2]);
            var across = Math.Abs(axis.Dot(towardViewer));
            if (across > AxisTolerance && across < 1 - AxisTolerance)
            {
                continue;
            }

            var edges = (face.GetEdges() as object[] ?? []).OfType<IEdge>().Where(e => ((ICurve)e.GetCurve()).IsCircle()).ToList();
            var around = edges.Sum(e => { var p = e.GetCurveParams3(); return ((ICurve)e.GetCurve()).GetLength3(p.UMinValue, p.UMaxValue); });
            if (around < 2 * Math.PI * c[6] * 0.99 || found.Any(known => SameAxis(known, origin, axis)))
            {
                continue;
            }

            found.Add(new ViewCylinder(face, edges.FirstOrDefault(), origin, axis, across >= 1 - AxisTolerance, c[6], Flat(origin), Flat(axis).Normalized()));
        }

        if (view.Type == (int)swDrawingViewTypes_e.swDrawingSectionView)
        {
            // A section shows only what's left after the cut, and cut faces aren't the model's own, so a hole counts
            // when a visible edge lies on its surface.
            var ends = (view.GetVisibleEntities2(null, (int)swViewEntityType_e.swViewEntityType_Edge) as object[] ?? []).OfType<IEdge>().Select(EndsOf).ToList();
            found.RemoveAll(cylinder => !ends.Any(end => OnSurface(cylinder, end.Start) && OnSurface(cylinder, end.End)));
        }

        // Holes one behind another share a centre line on the sheet.
        var kept = new List<ViewCylinder>();
        foreach (var cylinder in found.Where(c => c.EndOn || !kept.Any(known => !known.EndOn && SameSheetLine(known, c))))
        {
            kept.Add(cylinder);
        }

        return kept;
    }

    private static bool OnSurface(ViewCylinder cylinder, Point3 point)
    {
        var between = point.Minus(cylinder.Point);
        var along = between.Dot(cylinder.Axis);
        var apart = between.Minus(new Point3(cylinder.Axis.X * along, cylinder.Axis.Y * along, cylinder.Axis.Z * along)).Length;
        return Math.Abs(apart - cylinder.Radius) < SurfaceTolerance;
    }

    private static bool SameSheetLine(ViewCylinder known, ViewCylinder other)
    {
        var between = other.OnSheet.Minus(known.OnSheet);
        var acrossLine = Math.Abs((between.X * known.SheetAxis.Y) - (between.Y * known.SheetAxis.X));
        return acrossLine < MarkPositionTolerance && Math.Abs(known.SheetAxis.Dot(other.SheetAxis)) > 1 - AxisTolerance;
    }

    private static bool SameAxis(ViewCylinder known, Point3 origin, Point3 axis)
    {
        var between = origin.Minus(known.Point);
        var along = between.Dot(known.Axis);
        var apart = between.Minus(new Point3(known.Axis.X * along, known.Axis.Y * along, known.Axis.Z * along)).Length;
        return apart < MarkPositionTolerance && Math.Abs(axis.Dot(known.Axis)) > 1 - AxisTolerance;
    }

    // A mark's position is in the view's own axes, unscaled, measured from the view's centre on the sheet. Its depth
    // means nothing here.
    private static bool HasCentreMark(IView view, ViewCylinder cylinder)
    {
        var transform = (double[])view.ModelToViewTransform.ArrayData;
        var centre = (double[])view.Position;
        var scale = transform[12];
        var x = cylinder.OnSheet.X + ((transform[9] - centre[0]) / scale);
        var y = cylinder.OnSheet.Y + ((transform[10] - centre[1]) / scale);
        return CentreMarksOf(view).Any(mark => mark.GetPosition(0) is double[] { Length: >= 2 } at
            && Math.Abs(at[0] - x) < MarkPositionTolerance && Math.Abs(at[1] - y) < MarkPositionTolerance);
    }

    private sealed record ViewCylinder(IFace2 Face, IEdge? Edge, Point3 Point, Point3 Axis, bool EndOn, double Radius, Point3 OnSheet, Point3 SheetAxis);

    private sealed record SheetFormatFacts(SheetBox? Frame, IReadOnlyList<SheetBox> TitleBlock, IReadOnlyList<INote> Notes, string? FormatUnits);
}
