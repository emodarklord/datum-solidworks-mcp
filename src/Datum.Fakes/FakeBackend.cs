using Datum.Core;

namespace Datum.Fakes;

public sealed class FakeBackend : ISolidWorksBackend
{
    private static int _nextId;

    private readonly object _gate = new();
    private readonly List<FakeDocument> _documents = [];
    private readonly Dictionary<string, FakeGeometry> _geometry = [];
    private FakeDocument? _active;
    private bool _hung;
    private int _rejectNext;
    private int _failNextFeature;
    private int _failNextRebuild;

    public SolidWorksInfo Info { get; set; } = new(SolidWorksRevision.Parse("34.4.1"), LicenseType.Student, 4242, []);

    public ErrorCode? ConnectFailure { get; set; }

    public IReadOnlyList<BlockingDialog> Dialogs { get; set; } = [];

    public bool Connected { get; private set; }

    public bool Disposed { get; private set; }

    public void Hang()
    {
        lock (_gate)
        {
            _hung = true;
        }
    }

    public void Release()
    {
        lock (_gate)
        {
            _hung = false;
            Monitor.PulseAll(_gate);
        }
    }

    public void RejectNextCall() => Volatile.Write(ref _rejectNext, 1);

    public void Connect(bool launch)
    {
        Enter();
        if (ConnectFailure is { } code)
        {
            throw new DatumException(code, "Fake connect failure");
        }

        Connected = true;
    }

    public SolidWorksInfo ReadInfo()
    {
        Enter();
        return Info;
    }

    public IReadOnlyList<DocumentInfo> ListDocuments()
    {
        Enter();
        lock (_documents)
        {
            return [.. _documents.Select(Describe)];
        }
    }

    public DocumentInfo NewDocument(DocumentType type)
    {
        Enter();
        lock (_documents)
        {
            var document = new FakeDocument(NextId(), $"{type}{_documents.Count + 1}", null, type);
            _documents.Add(document);
            _active = document;
            return Describe(document);
        }
    }

    public DocumentInfo OpenDocument(string path, bool openReadOnly)
    {
        Enter();
        if (!File.Exists(path))
        {
            throw new DatumException(ErrorCode.NotFound, $"no file at '{path}'");
        }

        lock (_documents)
        {
            var document = _documents.FirstOrDefault(d => string.Equals(d.Path, path, StringComparison.OrdinalIgnoreCase))
                ?? new FakeDocument(NextId(), Path.GetFileName(path), path, DocumentTypes.FromPath(path) ?? DocumentType.Part) { ReadOnly = openReadOnly };
            if (!_documents.Contains(document))
            {
                _documents.Add(document);
            }

            _active = document;
            return Describe(document);
        }
    }

    public DocumentInfo SaveDocument(string id, string? newPath)
    {
        Enter();
        lock (_documents)
        {
            var document = Find(id);
            var path = newPath ?? document.Path ?? throw new DatumException(ErrorCode.SolidworksError, "document has never been saved");
            File.WriteAllText(path, $"fake {document.Type} saved {DateTime.UtcNow:O}");
            document.Path = path;
            document.Title = Path.GetFileName(path);
            document.Modified = false;
            return Describe(document);
        }
    }

    public void ExportDocument(string id, string path)
    {
        Enter();
        lock (_documents)
        {
            File.WriteAllText(path, $"fake export of {Find(id).Title}");
        }
    }

    public DocumentInfo ActivateDocument(string id)
    {
        Enter();
        lock (_documents)
        {
            _active = Find(id);
            return Describe(_active);
        }
    }

    public void CloseDocument(string id)
    {
        Enter();
        lock (_documents)
        {
            var document = Find(id);
            _documents.Remove(document);
            if (_active == document)
            {
                _active = _documents.LastOrDefault();
            }
        }
    }

    // Simulates the user editing a document in SOLIDWORKS.
    public void MarkModified(string id)
    {
        lock (_documents)
        {
            Find(id).Modified = true;
        }
    }

    public FakeGeometry GeometryOf(string id)
    {
        lock (_documents)
        {
            Find(id);
            if (!_geometry.TryGetValue(id, out var geometry))
            {
                _geometry[id] = geometry = new FakeGeometry();
            }

            return geometry;
        }
    }

    public IReadOnlyList<FeatureInfo> ListFeatures(string id)
    {
        Enter();
        return [.. GeometryOf(id).Features];
    }

    public ModelInfo ReadModel(string id)
    {
        Enter();
        lock (_documents)
        {
            var document = Find(id);
            return GeometryOf(id).Model ?? new ModelInfo(document.Title, document.Type, null, 0, 0, 0, default, null, 0);
        }
    }

    public IReadOnlyList<FaceInfo> ListFaces(string id, string? feature = null)
    {
        Enter();
        return [.. GeometryOf(id).Faces];
    }

    public IReadOnlyList<EdgeInfo> ListEdges(string id)
    {
        Enter();
        return [.. GeometryOf(id).Edges];
    }

    public List<(StandardView View, int Width, int Height)> Captures { get; } = [];

    public IReadOnlyList<string> MissingOnOpen { get; set; } = [];

    public CaptureOptions? LastCapture { get; private set; }

    public (IReadOnlyList<string>? Names, double Percent, int Limit)? LastFlex { get; private set; }

    // A slice of IFeature, enough to exercise the gateway tools without SOLIDWORKS.
    private static readonly ApiMember[] FeatureMembers =
    [
        new("IFeature", "GetTypeName2", ApiMemberKind.Method, [], "string", true, false),
        new("IFeature", "Name", ApiMemberKind.Property, [], "string", true, true),
        new("IFeature", "GetDefinition", ApiMemberKind.Method, [], "object", true, false),
    ];

    public void RunMacro(string path, string moduleName, string procedure) => Enter();

    private Action<SolidWorksEvent>? _sink;

    public void Subscribe(Action<SolidWorksEvent> sink)
    {
        Enter();
        _sink = sink;
    }

    public IReadOnlyList<EquationInfo> ListEquations(string id)
    {
        Enter();
        return [.. GeometryOf(id).Equations];
    }

    public EquationInfo SetEquation(string id, string text, IReadOnlyList<string>? configurations)
    {
        Enter();
        var equations = GeometryOf(id).Equations;
        equations.RemoveAll(e => e.Left == Equations.LeftOf(text));
        var set = new EquationInfo(equations.Count, text, 0, text.StartsWith('"') && !text.Contains('@', StringComparison.Ordinal));
        equations.Add(set);
        return set;
    }

    public void DeleteEquation(string id, string left)
    {
        Enter();
        if (GeometryOf(id).Equations.RemoveAll(e => e.Left == left) == 0)
        {
            throw new DatumException(ErrorCode.NotFound, $"no equation for '{left}'", "equation_list shows them");
        }
    }

    public IReadOnlyList<FaceProbe> ProbeFaces(string id, Point3 pull)
    {
        Enter();
        return [.. GeometryOf(id).Probes];
    }

    public SheetBends? ReadSheetBends(string id)
    {
        Enter();
        return GeometryOf(id).Bends;
    }

    public string? ActiveMotionStudy(string id)
    {
        Enter();
        return ShowingStudy;
    }

    public string? ShowingStudy { get; set; }

    public Lesson ReadLesson(string id, IReadOnlyList<string> features)
    {
        Enter();
        var lesson = GeometryOf(id).Lesson;
        return lesson with { Steps = [.. lesson.Steps.Where(s => s.Made is null || features.Contains(s.Made))] };
    }

    public void Raise(string kind, string document, string? detail = null) => _sink?.Invoke(new SolidWorksEvent(DateTimeOffset.Now, kind, document, detail));

    public IReadOnlyList<ApiEntry> ApiEntries()
    {
        Enter();
        return [new ApiEntry("IFeatureManager", "FeatureBossThicken2", "FeatureBossThicken2(...) → Feature")];
    }

    public IReadOnlyList<ApiMember> DescribeMembers(string? id, string typeOrHandle, string? query)
    {
        Enter();
        var type = typeOrHandle.StartsWith("feat#", StringComparison.OrdinalIgnoreCase) ? "IFeature" : typeOrHandle.StartsWith('I') ? typeOrHandle : "I" + typeOrHandle;
        if (type != "IFeature")
        {
            throw new DatumException(ErrorCode.NotFound, $"no interface '{typeOrHandle}' in the SOLIDWORKS API");
        }

        return [.. FeatureMembers.Where(m => query is null || m.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];
    }

    public ApiResult Invoke(string? id, ApiCall request, GatewayWrites writes)
    {
        Enter();
        Gateway.RequireNotDenied(request.Member);
        var features = GeometryOf(id ?? throw new DatumException(ErrorCode.NotFound, "no document")).Features;
        var index = features.FindIndex(f => string.Equals(f.Handle, request.Target, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new DatumException(ErrorCode.NotFound, $"no '{request.Target}' in {id}");
        }

        var member = FeatureMembers.FirstOrDefault(m => m.Name == request.Member)
            ?? throw new DatumException(ErrorCode.NotFound, $"IFeature has no member {request.Member}", "Call api_members IFeature");
        var setting = request.Value is not null;
        if (!Gateway.IsRead(member.Kind, member.Name, setting))
        {
            Gateway.RequireWrites(writes, member.Name);
        }

        var feature = features[index];
        var volume = GeometryOf(id).Model?.VolumeMm3 ?? 0;
        switch (member.Name)
        {
            case "Name" when setting:
                features[index] = feature with { Name = request.Value!.Value.GetString()! };
                return new ApiResult("set", [], true, new ChangeSummary(volume, volume, 1, []));
            case "Name":
                return new ApiResult($"\"{feature.Name}\"", [], false, null);
            case "GetTypeName2":
                return new ApiResult($"\"{feature.TypeName}\"", [], false, null);
            default:
                return new ApiResult($"obj#{Interlocked.Increment(ref _nextId)} (IFakeFeatureData)", [], false, null);
        }
    }

    // Each view is filled with its own grey level (front 10, back 20, …) so tests can tell them apart.
    public ViewCapture CaptureViews(string id, IReadOnlyList<StandardView> views, int width, int height, CaptureOptions options)
    {
        Enter();
        var geometry = GeometryOf(id);
        Captures.AddRange(views.Select(v => (v, width, height)));
        LastCapture = options;
        IReadOnlyList<ImageLabel> labels = options.Labels == CaptureLabels.None ? [] : [new ImageLabel("face#7", 10, 10)];
        return new ViewCapture([.. views.Select(v => Raster.Filled(width, height, (byte)((int)v * 10), (byte)((int)v * 10), (byte)((int)v * 10)))], geometry.ActiveSketch is not null, labels);
    }

    public IReadOnlyList<SelectionItem> ReadSelection(string id)
    {
        Enter();
        return [.. GeometryOf(id).Selection.Select(h => new SelectionItem(h, h[..h.IndexOf('#', StringComparison.Ordinal)]))];
    }

    public void SelectEntities(string id, IReadOnlyList<string> entityHandles)
    {
        Enter();
        var geometry = GeometryOf(id);
        var known = geometry.Faces.Select(f => f.Handle).Concat(geometry.Edges.Select(e => e.Handle)).Concat(geometry.Features.Select(f => f.Handle));
        if (entityHandles.FirstOrDefault(h => !known.Contains(h)) is { } unknown)
        {
            throw new DatumException(ErrorCode.NotFound, $"no '{unknown}' in {id}", "Call model_faces or model_tree for current handles");
        }

        geometry.Selection.Clear();
        geometry.Selection.AddRange(entityHandles);
    }

    public SketchInfo BeginSketch(string id, string target)
    {
        Enter();
        var geometry = GeometryOf(id);
        if (geometry.ActiveSketch is { } open)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{open.Name} is still being edited", "Call sketch_end first");
        }

        geometry.ActiveSketch = new FakeSketch($"Sketch{geometry.Features.Count(f => f.TypeName == "ProfileFeature") + 1}", target);
        return Describe(geometry.ActiveSketch);
    }

    public SketchInfo ReopenSketch(string id, string sketch)
    {
        Enter();
        var geometry = GeometryOf(id);
        if (geometry.ActiveSketch is { } open)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{open.Name} is still being edited", "Call sketch_end first");
        }

        var feature = geometry.Features.FirstOrDefault(f => f.Handle == sketch && f.TypeName == "ProfileFeature")
            ?? throw new DatumException(ErrorCode.InvalidArgument, $"{sketch} is not a sketch", "Give a sketch's feat# from model_tree");
        geometry.ActiveSketch = new FakeSketch(feature.Name, sketch) { Reopened = sketch };
        return Describe(geometry.ActiveSketch) with { Handle = sketch };
    }

    public IReadOnlyList<SketchSegmentInfo> AddSketchEntities(string id, IReadOnlyList<SketchEntitySpec> entities)
    {
        Enter();
        var sketch = ActiveSketch(id);
        var created = new List<SketchSegmentInfo>();
        foreach (var entity in entities)
        {
            var segments = SegmentsFor(entity);
            sketch.Segments.AddRange(segments);
            sketch.Undimensioned += entity.Dimension == false ? segments.Count : 0;
            created.AddRange(segments);
        }

        return created;
    }

    public SketchInfo ConstrainSketch(string id, IReadOnlyList<SketchConstraintSpec> constraints)
    {
        Enter();
        var sketch = ActiveSketch(id);
        var known = sketch.Segments.Select(s => s.Handle).Append(SketchSpecs.Origin).ToHashSet();
        if (constraints.SelectMany(c => c.Entities).FirstOrDefault(h => !known.Contains(h)) is { } unknown)
        {
            throw new DatumException(ErrorCode.NotFound, $"no '{unknown}' in {sketch.Name}");
        }

        sketch.Undimensioned = Math.Max(0, sketch.Undimensioned - constraints.Count);
        return Describe(sketch);
    }

    public SketchInfo ReadSketch(string id)
    {
        Enter();
        return Describe(ActiveSketch(id));
    }

    public SketchInfo EndSketch(string id)
    {
        Enter();
        var geometry = GeometryOf(id);
        var sketch = ActiveSketch(id);
        geometry.ActiveSketch = null;
        if (sketch.Reopened is { } existing)
        {
            return Describe(sketch) with { Handle = existing, Reopened = true };
        }

        var handle = $"feat#{Interlocked.Increment(ref _nextId)}";
        geometry.Features.Add(new FeatureInfo(handle, sketch.Name, "ProfileFeature", 0, false, null));
        return Describe(sketch) with { Handle = handle };
    }

    public IReadOnlyList<WeldmentProfile> WeldmentProfiles()
    {
        Enter();
        return [new WeldmentProfile("iso", "square tube", @"C:\profiles\iso\square tube.sldlfp")];
    }

    public IReadOnlyList<string> WeldmentSizes(string profile)
    {
        Enter();
        return ["40 x 40 x 4", "50 x 50 x 5"];
    }

    public IReadOnlyList<CutListItem> CutList(string id)
    {
        Enter();
        return GeometryOf(id).CutList;
    }

    public string SetFlat(string id, bool flat)
    {
        Enter();
        return "Flat-Pattern1";
    }

    public void ExportFlatPattern(string id, string path)
    {
        Enter();
        File.WriteAllText(path, "0\nSECTION\n2\nENTITIES\n0\nLINE\n8\n0\n10\n0\n20\n0\n11\n100\n21\n50\n0\nENDSEC\n");
    }

    public FeatureResult CreateFeature(string id, FeatureSpec spec)
    {
        Enter();
        var geometry = GeometryOf(id);
        if (Interlocked.Exchange(ref _failNextFeature, 0) == 1)
        {
            throw new DatumException(ErrorCode.FeatureFailed, $"SOLIDWORKS couldn't make the {spec.Kind}: fake failure", spec.FailureHint);
        }

        var model = geometry.Model ?? ReadModel(id);
        var before = model.VolumeMm3;
        var after = before + (spec is ExtrudeSpec { Cut: false } or RevolveSpec { Cut: false } or PatternSpec or MirrorSpec or SweepSpec or LoftSpec ? 1000 : -100);
        geometry.Model = model with { VolumeMm3 = after, Bodies = Math.Max(1, model.Bodies) };
        var handle = $"feat#{Interlocked.Increment(ref _nextId)}";
        var name = $"{spec.Kind}{geometry.Features.Count + 1}";
        geometry.Features.Add(new FeatureInfo(handle, name, spec.GetType().Name.Replace("Spec", "", StringComparison.Ordinal), 0, false, null));
        geometry.FeatureVolumes[handle] = after - before;
        return new FeatureResult(handle, name, new ChangeSummary(before, after, Math.Max(1, model.Bodies), []), [handle]);
    }

    public void FailNextFeature() => Volatile.Write(ref _failNextFeature, 1);

    public void FailNextRebuild() => Volatile.Write(ref _failNextRebuild, 1);

    public Measurement Measure(string id, IReadOnlyList<string> entities)
    {
        Enter();
        var geometry = GeometryOf(id);
        var missing = entities.Where(handle => handle.Contains('#', StringComparison.Ordinal)).FirstOrDefault(handle => !geometry.Faces.Any(f => f.Handle == handle) && !geometry.Edges.Any(e => e.Handle == handle) && !geometry.Features.Any(f => f.Handle == handle));
        return missing is null
            ? geometry.Measured ?? new Measurement(null, null, null, null, null, null, null, null, null)
            : throw new DatumException(ErrorCode.NotFound, $"no entity {missing}");
    }

    public SketchDetail ReadSketchDetail(string id, string sketch)
    {
        Enter();
        return GeometryOf(id).SketchDetails.TryGetValue(sketch, out var detail)
            ? detail
            : throw new DatumException(ErrorCode.InvalidArgument, $"{sketch} is not a sketch", "Give a sketch's feat# from model_tree");
    }

    public string? ReferenceOf(string id, string handle)
    {
        Enter();
        return handle.Contains('#', StringComparison.Ordinal) ? $"ref:{handle}" : null;
    }

    public string HandleFromReference(string id, string reference)
    {
        Enter();
        return reference[4..];
    }

    public ModelFacts ReadFacts(string id)
    {
        Enter();
        return GeometryOf(id).Facts;
    }

    public FlexResult FlexDimensions(string id, IReadOnlyList<string>? names, double percent, int limit)
    {
        Enter();
        var geometry = GeometryOf(id);
        LastFlex = (names, percent, limit);
        return geometry.Flexed ?? new FlexResult([], 0, null);
    }

    public ComponentInfo AddComponent(string id, string path, Point3 atMm, string? configuration)
    {
        Enter();
        var geometry = GeometryOf(id);
        if (Describe(_documents.First(d => d.Id == id)).Type != DocumentType.Assembly)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{id} is not an assembly");
        }

        var name = $"{Path.GetFileNameWithoutExtension(path)}-{geometry.Components.Count + 1}";
        var added = new ComponentInfo($"comp#{Interlocked.Increment(ref _nextId)}", name, path, configuration ?? "Default", false, false, "under defined", new ComponentPose(atMm, default));
        geometry.Components.Add(added);
        return added;
    }

    public IReadOnlyList<ComponentInfo> ListComponents(string id)
    {
        Enter();
        return [.. GeometryOf(id).Components];
    }

    public ComponentInfo SetComponentFixed(string id, string component, bool isFixed)
    {
        Enter();
        return Replace(id, component, c => c with { Fixed = isFixed });
    }

    public ComponentInfo SetComponentFlexible(string id, string component, bool flexible)
    {
        Enter();
        return Replace(id, component, c => c with { Flexible = flexible });
    }

    public ComponentMove MoveComponent(string id, string component, ComponentPose pose)
    {
        Enter();
        var before = FindComponent(id, component);
        if (before.Fixed)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"{component} is fixed", "Call component_fix with isFixed false first");
        }

        if (GeometryOf(id).HeldComponents.Contains(component))
        {
            return new ComponentMove(before.Pose, before.Pose, true);
        }

        return new ComponentMove(before.Pose, Replace(id, component, c => c with { Pose = pose }).Pose, false);
    }

    public void RemoveComponent(string id, string component)
    {
        Enter();
        GeometryOf(id).Components.Remove(FindComponent(id, component));
    }

    private ComponentInfo FindComponent(string id, string handle) =>
        GeometryOf(id).Components.FirstOrDefault(c => c.Handle == handle) ?? throw new DatumException(ErrorCode.NotFound, $"no '{handle}'");

    private ComponentInfo Replace(string id, string handle, Func<ComponentInfo, ComponentInfo> change)
    {
        var components = GeometryOf(id).Components;
        var at = components.IndexOf(FindComponent(id, handle));
        components[at] = change(components[at]);
        return components[at];
    }

    public IReadOnlyList<FaceInfo> ListComponentFaces(string id, string component, string? feature = null)
    {
        Enter();
        FindComponent(id, component);
        return [.. GeometryOf(id).Faces];
    }

    public IReadOnlyList<EdgeInfo> ListComponentEdges(string id, string component)
    {
        Enter();
        FindComponent(id, component);
        return [.. GeometryOf(id).Edges];
    }

    public MateResult AddMate(string id, MateSpec spec)
    {
        Enter();
        var geometry = GeometryOf(id);
        if (Interlocked.Exchange(ref _failNextFeature, 0) == 1)
        {
            throw new DatumException(ErrorCode.FeatureFailed, $"SOLIDWORKS couldn't add the {spec.NormalizedKind} mate: fake failure");
        }

        var name = $"{char.ToUpperInvariant(spec.NormalizedKind[0])}{spec.NormalizedKind[1..]}{geometry.Mates.Count + 1}";
        var handle = $"feat#{Interlocked.Increment(ref _nextId)}";
        geometry.Mates.Add(new MateInfo(handle, name, spec.NormalizedKind, [spec.First, spec.Second], false, null));
        geometry.Features.Add(new FeatureInfo(handle, name, "Mate", 0, false, null));
        return new MateResult(handle, name, geometry.NextMateMoves, [.. geometry.Components.Where(c => !c.Fixed && c.Status != "fully defined").Select(c => c.Name)]);
    }

    public IReadOnlyList<MateInfo> ListMates(string id)
    {
        Enter();
        return [.. GeometryOf(id).Mates];
    }

    public IReadOnlyList<InterferencePair> FindInterference(string id)
    {
        Enter();
        return [.. GeometryOf(id).Interferences];
    }

    public IReadOnlyList<BomLine> ReadBom(string id)
    {
        Enter();
        return [.. GeometryOf(id).Components.GroupBy(c => c.Path ?? c.Name).Select(g => new BomLine(Path.GetFileName(g.Key), "Default", g.Count(), null, null))];
    }

    public IReadOnlyList<string> MissingFiles(string id)
    {
        Enter();
        return [.. GeometryOf(id).Missing, .. MissingOnOpen];
    }

    public IReadOnlyList<string> UnsavedReferences(string id)
    {
        Enter();
        return [.. GeometryOf(id).UnsavedReferences];
    }

    public IReadOnlyList<ConfigurationInfo> ListConfigurations(string id)
    {
        Enter();
        var geometry = GeometryOf(id);
        return [.. geometry.Configurations.Select(name => new ConfigurationInfo(name, name == geometry.ActiveConfiguration))];
    }

    public string AddConfiguration(string id, string name)
    {
        Enter();
        var geometry = GeometryOf(id);
        if (geometry.Configurations.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"a configuration named {name} already exists", "Use config_activate");
        }

        geometry.Configurations.Add(name);
        return geometry.ActiveConfiguration = name;
    }

    public string ActivateConfiguration(string id, string name)
    {
        Enter();
        var geometry = GeometryOf(id);
        return geometry.Configurations.Contains(name, StringComparer.OrdinalIgnoreCase)
            ? geometry.ActiveConfiguration = name
            : throw new DatumException(ErrorCode.NotFound, $"no configuration named {name}", "Call config_list");
    }

    public ExplodeResult CreateExplode(string id, IReadOnlyList<ExplodeStepSpec> steps)
    {
        Enter();
        var geometry = GeometryOf(id);
        if (!geometry.ExplodedConfigurations.Add(geometry.ActiveConfiguration))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"configuration {geometry.ActiveConfiguration} already has an exploded view");
        }

        geometry.ExplodeSteps = [.. steps.Select((s, i) => new ExplodeStepInfo($"Step{i + 1}", FindComponent(id, s.Component).Name, Math.Abs(s.Distance)))];
        return new ExplodeResult("ExplView1", geometry.ActiveConfiguration, geometry.ExplodeSteps);
    }

    public IReadOnlyList<ExplodeStepInfo> ReadExplodeSteps(string id, string configuration)
    {
        Enter();
        var geometry = GeometryOf(id);
        return geometry.Configurations.Contains(configuration, StringComparer.OrdinalIgnoreCase)
            ? geometry.ExplodeSteps
            : throw new DatumException(ErrorCode.NotFound, $"no configuration named {configuration}", "Call config_list");
    }

    public bool ShowExplode(string id, bool show)
    {
        Enter();
        var geometry = GeometryOf(id);
        return geometry.ExplodedConfigurations.Contains(geometry.ActiveConfiguration)
            ? show
            : throw new DatumException(ErrorCode.NotFound, $"configuration {geometry.ActiveConfiguration} has no exploded view", "Call explode_create first");
    }

    public DrawingCreated NewDrawing(DrawingSpec spec)
    {
        Enter();
        var document = NewDocument(DocumentType.Drawing);
        return new DrawingCreated(document, spec.SheetName == "a0" ? null : $"{spec.SheetName}.slddrt", 279.4, 215.9, spec.FirstAngle);
    }

    public IReadOnlyList<ViewInfo> AddStandardViews(string id, string model)
    {
        Enter();
        return [MakeView(id, model, "front", 100, 100), MakeView(id, model, "top", 100, 100), MakeView(id, model, "right", 100, 100), MakeView(id, model, "iso", 100, 100)];
    }

    public ViewInfo AddView(string id, string model, string orientation, double x, double y)
    {
        Enter();
        return MakeView(id, model, orientation, x, y);
    }

    public IReadOnlyList<ViewInfo> ListViews(string id)
    {
        Enter();
        return [.. GeometryOf(id).Views];
    }

    public ViewInfo SetView(string id, string view, double? x, double? y, string? scale, string? display, string? configuration = null, bool? exploded = null)
    {
        Enter();
        var views = GeometryOf(id).Views;
        var at = views.FindIndex(v => v.Handle == view);
        if (at < 0)
        {
            throw new DatumException(ErrorCode.NotFound, $"no '{view}'");
        }

        var old = views[at];
        return views[at] = old with { X = x ?? old.X, Y = y ?? old.Y, Scale = scale ?? old.Scale, Display = display ?? old.Display };
    }

    public IReadOnlyList<ViewEntity> ListViewEntities(string id, string view)
    {
        Enter();
        return [new ViewEntity("edge#500", "line", 10, 10, 60, 10)];
    }

    public ViewInfo AddSectionView(string id, string parent, bool vertical, double offset, double x, double y, string? label, bool flip)
    {
        Enter();
        return MakeChild(id, parent, "section", x, y);
    }

    public ViewInfo AddDetailView(string id, string parent, double atX, double atY, double radius, string scale, double x, double y, string? label)
    {
        Enter();
        return MakeChild(id, parent, "detail", x, y) with { Scale = scale };
    }

    public ViewInfo AddAuxiliaryView(string id, string parent, string edge, double x, double y, string? label)
    {
        Enter();
        return MakeChild(id, parent, "auxiliary", x, y);
    }

    private ViewInfo MakeChild(string id, string parent, string kind, double x, double y)
    {
        var geometry = GeometryOf(id);
        var source = geometry.Views.FirstOrDefault(v => v.Handle == parent) ?? throw new DatumException(ErrorCode.NotFound, $"no '{parent}'");
        var view = source with { Handle = $"view#{Interlocked.Increment(ref _nextId)}", Name = $"{kind} View", Kind = kind, X = x, Y = y };
        geometry.Views.Add(view);
        return view;
    }

    public IReadOnlyList<ViewInfo> ImportDimensions(string id, string? view)
    {
        Enter();
        var views = GeometryOf(id).Views;
        for (var i = 0; i < views.Count; i++)
        {
            if (view is null || views[i].Handle == view)
            {
                views[i] = views[i] with { Dimensions = views[i].Dimensions + 2 };
            }
        }

        return [.. views.Where(v => view is null || v.Handle == view)];
    }

    public IReadOnlyList<SheetDimension> ListSheetDimensions(string id, string? view)
    {
        Enter();
        return [.. GeometryOf(id).SheetDimensions.Where(d => view is null || d.View == view)];
    }

    public SheetDimension AddSheetDimension(string id, string view, string first, string? second, double x, double y)
    {
        Enter();
        var dimension = new SheetDimension(view, "RD1@Drawing View1", 25, " mm", false, null);
        GeometryOf(id).SheetDimensions.Add(dimension);
        return dimension;
    }

    public ToleranceChange SetTolerance(string id, string dimension, string kind, double upper, double lower)
    {
        Enter();
        return new ToleranceChange(dimension, kind, upper, lower, @"C:\Parts\plate.SLDPRT");
    }

    public MarkCount AddMarks(string id, string view)
    {
        Enter();
        return new MarkCount(view, 1, 2, 0, 0);
    }

    public string AddNote(string id, string text, double x, double y, string? view)
    {
        Enter();
        return text;
    }

    public DrawingFacts ReadDrawingFacts(string id)
    {
        Enter();
        return GeometryOf(id).DrawingFacts ?? new DrawingFacts(279.4, 215.9, [], [], [], [], false);
    }

    public TitleChange SetTitleField(string id, string name, string value)
    {
        Enter();
        return name switch
        {
            "DrawnBy" => new TitleChange(name, value, true, null),
            "Description" => new TitleChange(name, value, true, @"C:\Parts\plate.SLDPRT"),
            _ => throw new DatumException(ErrorCode.NotFound, $"the title block has no field '{name}'", "Fields: DrawnBy, Description"),
        };
    }

    public IReadOnlyList<BomRow> AddBom(string id, string view, double x, double y, string? bomTemplate)
    {
        Enter();
        GeometryOf(id).BomRows = [new BomRow("1", "plate", "", 1), new BomRow("2", "pin", "Pin", 2)];
        return GeometryOf(id).BomRows!;
    }

    public IReadOnlyList<BomRow> ReadBomTable(string id)
    {
        Enter();
        return GeometryOf(id).BomRows ?? throw new DatumException(ErrorCode.NotFound, "this drawing has no BOM table", "Call bom_add");
    }

    public IReadOnlyList<BalloonInfo> AddBalloons(string id, string view)
    {
        Enter();
        return [new BalloonInfo("1", "plate-1", false), new BalloonInfo("2", "pin-1", false)];
    }

    public IReadOnlyList<ExplodeLine> AddExplodeLines(string id)
    {
        Enter();
        return [.. GeometryOf(id).ExplodeSteps.Select((_, i) => new ExplodeLine($"pin-{i + 1}", new Point3(0, 0, 0), new Point3(0, 0, 40)))];
    }

    public string SetAppearance(string id, string? target, Colour colour)
    {
        Enter();
        return target ?? id;
    }

    public string NewMotionStudy(string id, string? name, double seconds)
    {
        Enter();
        var study = name ?? "Motion Study 1";
        GeometryOf(id).MotionStudies[study] = seconds;
        return study;
    }

    public string AddMotionMotor(string id, string study, MotionMotorSpec spec)
    {
        Enter();
        _ = StudySeconds(id, study);
        return "RotaryMotor1";
    }

    public void AddMotionPath(string id, string study, MotionPathSpec spec)
    {
        Enter();
        _ = StudySeconds(id, study);
    }

    public double MotionStudySeconds(string id, string study)
    {
        Enter();
        return StudySeconds(id, study);
    }

    public IReadOnlyList<MotionSample> RunMotionStudy(string id, string study, int samples, double? hold = null)
    {
        Enter();
        var seconds = StudySeconds(id, study);
        return [.. Enumerable.Range(1, samples).SelectMany(i => new[]
        {
            new MotionSample(seconds * i / samples, "wheel-1", 90, 0),
            new MotionSample(seconds * i / samples, "frame-1", 0, 0),
        })];
    }

    public IReadOnlyList<Raster> RenderTurntable(string id, double tilt, int width, int height, int frames)
    {
        Enter();
        return [.. Enumerable.Range(0, frames).Select(_ => new Raster(width, height, new byte[width * height * 3]))];
    }

    public IReadOnlyList<Raster> RenderMotionStudy(string id, string study, StandardView view, int width, int height, int frames)
    {
        Enter();
        _ = StudySeconds(id, study);
        return [.. Enumerable.Range(0, frames).Select(_ => new Raster(width, height, new byte[width * height * 3]))];
    }

    private double StudySeconds(string id, string study) =>
        GeometryOf(id).MotionStudies.TryGetValue(study, out var seconds) ? seconds : throw new DatumException(ErrorCode.NotFound, $"no motion study '{study}'", "Call motion_new");

    public IReadOnlyList<SheetAnnotation> ListAnnotations(string id, string? view)
    {
        Enter();
        return [.. GeometryOf(id).Annotations.Where(a => view is null || a.View == view)];
    }

    public SheetAnnotation MoveAnnotation(string id, string name, double x, double y)
    {
        Enter();
        var annotations = GeometryOf(id).Annotations;
        var at = annotations.FindIndex(a => a.Name == name);
        if (at < 0)
        {
            throw new DatumException(ErrorCode.NotFound, $"no annotation '{name}' on this drawing", "Call annotation_list");
        }

        return annotations[at] = annotations[at] with { X = x, Y = y };
    }

    public void DeleteAnnotation(string id, string name)
    {
        Enter();
        if (GeometryOf(id).Annotations.RemoveAll(a => a.Name == name) == 0)
        {
            throw new DatumException(ErrorCode.NotFound, $"no annotation '{name}' on this drawing", "Call annotation_list");
        }
    }

    public IReadOnlyList<string> DeleteView(string id, string view)
    {
        Enter();
        var views = GeometryOf(id).Views;
        var found = views.Find(v => v.Handle == view) ?? throw new DatumException(ErrorCode.NotFound, $"no '{view}'");
        views.Remove(found);
        return [found.Name];
    }

    public string SetSheetScale(string id, string scale)
    {
        Enter();
        return scale;
    }

    public void SetSheetUnits(string id, string units) => Enter();

    public SheetBox MoveBom(string id, double x, double y)
    {
        Enter();
        return GeometryOf(id).BomRows is null ? throw new DatumException(ErrorCode.NotFound, "this drawing has no BOM table", "Call bom_add") : new SheetBox(x, y - 40, x + 100, y);
    }

    public string EditNote(string id, string name, string text)
    {
        Enter();
        var annotations = GeometryOf(id).Annotations;
        var at = annotations.FindIndex(a => a.Name == name);
        if (at < 0)
        {
            throw new DatumException(ErrorCode.NotFound, $"no annotation '{name}' on this drawing", "Call annotation_list");
        }

        annotations[at] = annotations[at] with { Text = text };
        return text;
    }

    public ViewCapture CaptureSheet(string id, int width, int height)
    {
        Enter();
        return new ViewCapture([Raster.Filled(width, height, 255, 255, 255)], false, []);
    }

    private ViewInfo MakeView(string id, string model, string orientation, double x, double y)
    {
        var view = new ViewInfo($"view#{Interlocked.Increment(ref _nextId)}", $"Drawing View{GeometryOf(id).Views.Count + 1}", "Sheet1", "named", Path.GetFileName(model), "1:1", x, y, 50, 40, "removed", 0, 0, 0);
        GeometryOf(id).Views.Add(view);
        return view;
    }

    public MaterialChange SetMaterial(string id, string? name, string? database)
    {
        Enter();
        var geometry = GeometryOf(id);
        if (database is null && name is not null && !geometry.KnownMaterials.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            var closest = MaterialNames.Closest(geometry.KnownMaterials, name);
            throw new DatumException(ErrorCode.NotFound, $"no material named '{name}'", $"Closest names: {string.Join(", ", closest)}");
        }

        var model = geometry.Model ?? ReadModel(id);
        var old = model.Material;
        var mass = name is null ? model.MassGrams / 2 : model.MassGrams * 2;
        geometry.Model = model with { Material = name, MassGrams = mass };
        return new MaterialChange(old, old is null ? null : "fake database", name, model.MassGrams, mass);
    }

    public IReadOnlyList<CustomProperty> ListProperties(string id, string configuration)
    {
        Enter();
        return [.. GeometryOf(id).Properties.Select(p => new CustomProperty(p.Key, p.Value, p.Value))];
    }

    public PropertyChange? SetProperty(string id, string name, string? value, string configuration)
    {
        Enter();
        var properties = GeometryOf(id).Properties;
        var old = properties.GetValueOrDefault(name);
        if (value is null)
        {
            properties.Remove(name);
            return null;
        }

        properties[name] = value;
        return new PropertyChange(name, old, value);
    }

    public IReadOnlyList<DimensionInfo> ListDimensions(string id, string? feature)
    {
        Enter();
        var dimensions = GeometryOf(id).Dimensions;
        return [.. feature is null ? dimensions : dimensions.Where(d => d.Feature == feature)];
    }

    public DimensionChange SetDimension(string id, string name, double value)
    {
        Enter();
        var geometry = GeometryOf(id);
        var index = geometry.Dimensions.FindIndex(d => string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            throw new DatumException(ErrorCode.NotFound, $"no dimension '{name}'", "Call model_dimensions");
        }

        if (Interlocked.Exchange(ref _failNextRebuild, 0) == 1)
        {
            throw new DatumException(ErrorCode.RebuildFailed, $"setting {name} to {value} breaks the rebuild: Fillet1 (fake)", "The old value is back");
        }

        var old = geometry.Dimensions[index];
        geometry.Dimensions[index] = old with { Value = value };
        var before = VolumeOf(geometry);
        SetVolume(geometry, before * value / old.Value);
        return new DimensionChange(old.Value, Summary(geometry, before));
    }

    public SuppressionChange SetSuppression(string id, IReadOnlyList<string> features, bool suppressed)
    {
        Enter();
        var geometry = GeometryOf(id);
        var before = VolumeOf(geometry);
        var changed = new List<string>();
        foreach (var handle in features)
        {
            var index = FeatureIndex(geometry, handle);
            if (geometry.Features[index].Suppressed != suppressed)
            {
                geometry.Features[index] = geometry.Features[index] with { Suppressed = suppressed };
                SetVolume(geometry, VolumeOf(geometry) + ((suppressed ? -1 : 1) * geometry.FeatureVolumes.GetValueOrDefault(handle)));
                changed.Add(handle);
            }
        }

        return new SuppressionChange(changed, Summary(geometry, before));
    }

    public ChangeSummary DeleteFeatures(string id, IReadOnlyList<string> features, bool withAbsorbed)
    {
        Enter();
        var geometry = GeometryOf(id);
        foreach (var handle in features)
        {
            if (geometry.Dependants.GetValueOrDefault(handle)?.Except(features).ToList() is { Count: > 0 } dependants)
            {
                throw new DatumException(ErrorCode.InvalidArgument, $"{handle} has dependent features {string.Join(", ", dependants)}", "Delete those first, or suppress instead");
            }
        }

        var before = VolumeOf(geometry);
        foreach (var handle in features)
        {
            var index = FeatureIndex(geometry, handle);
            SetVolume(geometry, VolumeOf(geometry) - geometry.FeatureVolumes.GetValueOrDefault(handle));
            geometry.Features.RemoveAt(index);
        }

        return Summary(geometry, before);
    }

    public ChangeSummary Rebuild(string id)
    {
        Enter();
        var geometry = GeometryOf(id);
        return Summary(geometry, VolumeOf(geometry));
    }

    private static int FeatureIndex(FakeGeometry geometry, string handle)
    {
        var index = geometry.Features.FindIndex(f => string.Equals(f.Handle, handle, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? index : throw new DatumException(ErrorCode.NotFound, $"no '{handle}'", "Call model_tree for current handles");
    }

    private static double VolumeOf(FakeGeometry geometry) => geometry.Model?.VolumeMm3 ?? 0;

    private static void SetVolume(FakeGeometry geometry, double volume) =>
        geometry.Model = (geometry.Model ?? new ModelInfo("fake", DocumentType.Part, null, 0, 0, 0, default, null, 1)) with { VolumeMm3 = volume };

    private static ChangeSummary Summary(FakeGeometry geometry, double before) => new(before, VolumeOf(geometry), geometry.Model?.Bodies ?? 0, []);

    public IReadOnlyList<BlockingDialog> FindBlockingDialogs() => Dialogs;

    private FakeSketch ActiveSketch(string id) =>
        GeometryOf(id).ActiveSketch ?? throw new DatumException(ErrorCode.InvalidArgument, "no sketch is being edited", "Call sketch_begin first");

    private static SketchInfo Describe(FakeSketch sketch) =>
        new(null, sketch.Name, sketch.Undimensioned == 0 && sketch.Segments.Count > 0 ? "fully defined" : "under defined", sketch.Segments.Count, default, new(1, 0, 0), new(0, 1, 0));

    private static List<SketchSegmentInfo> SegmentsFor(SketchEntitySpec e)
    {
        SketchSegmentInfo Line(double x1, double y1, double x2, double y2) =>
            new($"seg#{Interlocked.Increment(ref _nextId)}", "line", new(x1, y1), new(x2, y2), null, null, e.Construction);

        return e.Type switch
        {
            "rect" => [Line(e.X1!.Value, e.Y1!.Value, e.X2!.Value, e.Y1!.Value), Line(e.X2!.Value, e.Y1!.Value, e.X2!.Value, e.Y2!.Value),
                Line(e.X2!.Value, e.Y2!.Value, e.X1!.Value, e.Y2!.Value), Line(e.X1!.Value, e.Y2!.Value, e.X1!.Value, e.Y1!.Value)],
            "circle" => [new($"seg#{Interlocked.Increment(ref _nextId)}", "circle", new(e.Cx!.Value + e.Radius!.Value, e.Cy!.Value),
                new(e.Cx!.Value + e.Radius!.Value, e.Cy!.Value), new(e.Cx!.Value, e.Cy!.Value), e.Radius, e.Construction)],
            "polyline" => [.. e.Points!.Zip(e.Points!.Skip(1).Concat(e.Closed ? [e.Points![0]] : []), (a, b) => Line(a[0], a[1], b[0], b[1]))],
            _ => [Line(e.X1 ?? e.Cx ?? 0, e.Y1 ?? e.Cy ?? 0, e.X2 ?? 0, e.Y2 ?? 0)],
        };
    }

    private static string NextId() => $"d{Interlocked.Increment(ref _nextId)}";

    private FakeDocument Find(string id) =>
        _documents.FirstOrDefault(d => d.Id == id) ?? throw new DatumException(ErrorCode.NotFound, $"no open document '{id}'");

    private DocumentInfo Describe(FakeDocument d) => new(d.Id, d.Title, d.Path, d.Type, d.Modified, d == _active, d.ReadOnly);

    public void Dispose()
    {
        Disposed = true;
        Release();
    }

    private void Enter()
    {
        lock (_gate)
        {
            while (_hung)
            {
                Monitor.Wait(_gate);
            }
        }

        if (Interlocked.Exchange(ref _rejectNext, 0) == 1)
        {
            throw new DatumException(ErrorCode.DialogBlocking, "SOLIDWORKS rejected the call");
        }
    }
}

internal sealed class FakeDocument(string id, string title, string? path, DocumentType type)
{
    public string Id { get; } = id;

    public string Title { get; set; } = title;

    public string? Path { get; set; } = path;

    public DocumentType Type { get; } = type;

    public bool Modified { get; set; }

    public bool ReadOnly { get; init; }
}

public sealed class FakeGeometry
{
    public List<FeatureInfo> Features { get; } = [];

    public List<FaceInfo> Faces { get; } = [];

    public List<EdgeInfo> Edges { get; } = [];

    public List<string> Selection { get; } = [];

    public ModelInfo? Model { get; set; }

    public FakeSketch? ActiveSketch { get; set; }

    public List<DimensionInfo> Dimensions { get; } = [];

    public List<ComponentInfo> Components { get; } = [];

    public List<ViewInfo> Views { get; } = [];

    public IReadOnlyList<BomRow>? BomRows { get; set; }

    public DrawingFacts? DrawingFacts { get; set; }

    public List<SheetDimension> SheetDimensions { get; } = [];

    public List<SheetAnnotation> Annotations { get; } = [];

    public Dictionary<string, double> MotionStudies { get; } = [];

    public List<string> Configurations { get; } = ["Default"];

    public string ActiveConfiguration { get; set; } = "Default";

    public HashSet<string> ExplodedConfigurations { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ExplodeStepInfo> ExplodeSteps { get; set; } = [];

    public List<InterferencePair> Interferences { get; } = [];

    public List<string> Missing { get; } = [];

    public List<string> UnsavedReferences { get; } = [];

    public List<MateInfo> Mates { get; } = [];

    public List<CutListItem> CutList { get; } = [];

    public List<EquationInfo> Equations { get; } = [];

    public List<FaceProbe> Probes { get; } = [];

    public SheetBends? Bends { get; set; }

    public Lesson Lesson { get; set; } = new([], [], [], []);

    public List<ComponentMoved> NextMateMoves { get; set; } = [];

    public HashSet<string> HeldComponents { get; } = [];

    public Measurement? Measured { get; set; }

    public FlexResult? Flexed { get; set; }

    public List<string> KnownMaterials { get; } = ["AISI 304", "AISI 316 Stainless Steel Sheet", "ABS"];

    public Dictionary<string, string> Properties { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, SketchDetail> SketchDetails { get; } = new(StringComparer.OrdinalIgnoreCase);

    public ModelFacts Facts { get; set; } = new("mm", "Default", 1, []);

    // Volume each feature added, so suppressing or deleting it can take that back out.
    public Dictionary<string, double> FeatureVolumes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, List<string>> Dependants { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class FakeSketch(string name, string target)
{
    public string Name { get; } = name;

    public string Target { get; } = target;

    // The handle of the finished sketch this edit reopened; null for a new sketch.
    public string? Reopened { get; init; }

    public List<SketchSegmentInfo> Segments { get; } = [];

    // Entities added with dimension: false; the fake calls a sketch fully defined once this reaches 0.
    public int Undimensioned { get; set; }
}
