namespace Datum.Core;

public interface ISolidWorksBackend : IDisposable
{
    // Everything except FindBlockingDialogs runs on the STA dispatcher thread.
    void Connect(bool launch);

    SolidWorksInfo ReadInfo();

    IReadOnlyList<DocumentInfo> ListDocuments();

    DocumentInfo NewDocument(DocumentType type);

    DocumentInfo OpenDocument(string path, bool openReadOnly);

    // newPath null saves in place; otherwise saves to that native path and the document moves there.
    DocumentInfo SaveDocument(string id, string? newPath);

    // Writes a copy in the format given by the extension; the document keeps its own path.
    void ExportDocument(string id, string path);

    DocumentInfo ActivateDocument(string id);

    void CloseDocument(string id);

    // Handles (face#12, edge#3, feat#7) are unique within the server process and stay the
    // same for the same entity across calls and rebuilds.
    IReadOnlyList<FeatureInfo> ListFeatures(string id);

    ModelInfo ReadModel(string id);

    // feature, when given, keeps the work down on big parts: only that feature's faces get described.
    IReadOnlyList<FaceInfo> ListFaces(string id, string? feature = null);

    IReadOnlyList<EdgeInfo> ListEdges(string id);

    IReadOnlyList<SelectionItem> ReadSelection(string id);

    // Replaces the current selection; throws NOT_FOUND for handles that don't resolve in that document.
    void SelectEntities(string id, IReadOnlyList<string> entityHandles);

    // target: Front, Top or Right, or a plane (feat#) or planar face (face#) handle. Sketch coordinates are mm in sketch space.
    SketchInfo BeginSketch(string id, string target);

    // Opens a finished sketch for editing; sketch_add, sketch_constrain and sketch_end then work on it.
    SketchInfo ReopenSketch(string id, string sketch);

    // Callers validate the specs first (SketchSpecs.Validate) so a bad batch never half-draws.
    IReadOnlyList<SketchSegmentInfo> AddSketchEntities(string id, IReadOnlyList<SketchEntitySpec> entities);

    SketchInfo ConstrainSketch(string id, IReadOnlyList<SketchConstraintSpec> constraints);

    SketchInfo ReadSketch(string id);

    SketchInfo EndSketch(string id);

    // Throws FEATURE_FAILED after removing anything it created when SOLIDWORKS can't build the feature.
    FeatureResult CreateFeature(string id, FeatureSpec spec);

    IReadOnlyList<WeldmentProfile> WeldmentProfiles();

    IReadOnlyList<string> WeldmentSizes(string profile);

    IReadOnlyList<CutListItem> CutList(string id);

    string SetFlat(string id, bool flat);

    void ExportFlatPattern(string id, string path);

    // Driving dimensions only, in mm or degrees; feature limits it to one feature and its sketches.
    IReadOnlyList<DimensionInfo> ListDimensions(string id, string? feature);

    // Puts the old value back and throws REBUILD_FAILED if the new one breaks the rebuild.
    DimensionChange SetDimension(string id, string name, double value);

    SuppressionChange SetSuppression(string id, IReadOnlyList<string> features, bool suppressed);

    // Refuses features other features depend on. withAbsorbed also removes their sketches.
    ChangeSummary DeleteFeatures(string id, IReadOnlyList<string> features, bool withAbsorbed);

    ChangeSummary Rebuild(string id);

    // One image per view at one scale, each centred on the model at width × height. The user's view is put back afterwards.
    ViewCapture CaptureViews(string id, IReadOnlyList<StandardView> views, int width, int height, CaptureOptions options);

    // One entity on its own, or the distance and angle between two. Leaves the user's selection as it was.
    Measurement Measure(string id, IReadOnlyList<string> entities);

    // Works on a finished sketch without opening it for editing.
    SketchDetail ReadSketchDetail(string id, string sketch);

    ModelFacts ReadFacts(string id);

    // A form of a handle that survives closing the document; null for entities with no persistent reference.
    string? ReferenceOf(string id, string handle);

    // Throws NOT_FOUND if the entity is gone.
    string HandleFromReference(string id, string reference);

    // name null clears the material. database null looks the name up in the installed databases and throws
    // NOT_FOUND, naming the closest matches, if it isn't there. The material is read back before returning.
    MaterialChange SetMaterial(string id, string? name, string? database);

    // target is null for the whole document, or a comp#, feat# or face# handle. Returns what was coloured.
    string SetAppearance(string id, string? target, Colour colour);

    // configuration "" means the file's own properties.
    IReadOnlyList<CustomProperty> ListProperties(string id, string configuration);

    // value null removes the property and returns null.
    PropertyChange? SetProperty(string id, string name, string? value, string configuration);

    // Tries each dimension up and down by percent, then puts every value back. names null takes all driving
    // dimensions, up to limit. The saved file isn't touched.
    FlexResult FlexDimensions(string id, IReadOnlyList<string>? names, double percent, int limit);

    // The file is loaded just long enough to add it, and isn't changed.
    ComponentInfo AddComponent(string id, string path, Point3 atMm, string? configuration);

    // Top-level components only.
    IReadOnlyList<ComponentInfo> ListComponents(string id);

    ComponentInfo SetComponentFixed(string id, string component, bool isFixed);

    // Flexible lets a sub-assembly's own mates move inside this assembly; rigid solves it as one piece.
    ComponentInfo SetComponentFlexible(string id, string component, bool flexible);

    // Reports where the component ended up, which mates may have decided.
    ComponentMove MoveComponent(string id, string component, ComponentPose pose);

    void RemoveComponent(string id, string component);

    // A component's faces and edges in assembly coordinates, with handles mates accept.
    IReadOnlyList<FaceInfo> ListComponentFaces(string id, string component, string? feature = null);

    IReadOnlyList<EdgeInfo> ListComponentEdges(string id, string component);

    // Removes the mate and puts components back, then throws FEATURE_FAILED, if it fails or breaks the rebuild.
    MateResult AddMate(string id, MateSpec spec);

    IReadOnlyList<MateInfo> ListMates(string id);

    // At the assembly's current pose only.
    IReadOnlyList<InterferencePair> FindInterference(string id);

    // Every suppressed-free component in the assembly, sub-assemblies included, grouped by file and configuration.
    IReadOnlyList<BomLine> ReadBom(string id);

    // Files the document references that aren't on disk.
    IReadOnlyList<string> MissingFiles(string id);

    // Referenced documents with changes that saving this document won't write.
    IReadOnlyList<string> UnsavedReferences(string id);

    IReadOnlyList<ConfigurationInfo> ListConfigurations(string id);

    // Both return the name of the configuration that is active afterwards, checked by reading it back.
    string AddConfiguration(string id, string name);

    string ActivateConfiguration(string id, string name);

    // In the active configuration, which must not have an exploded view yet.
    ExplodeResult CreateExplode(string id, IReadOnlyList<ExplodeStepSpec> steps);

    // The steps of a configuration's exploded view; empty if it has none. Leaves the active configuration as it was.
    IReadOnlyList<ExplodeStepInfo> ReadExplodeSteps(string id, string configuration);

    // Returns whether the assembly now shows exploded.
    bool ShowExplode(string id, bool show);

    DrawingCreated NewDrawing(DrawingSpec spec);

    // Front, top, right at the sheet's projection, plus an isometric. Returns the new views.
    IReadOnlyList<ViewInfo> AddStandardViews(string id, string model);

    // Sheet position in mm from the bottom left.
    ViewInfo AddView(string id, string model, string orientation, double x, double y);

    IReadOnlyList<ViewInfo> ListViews(string id);

    ViewInfo SetView(string id, string view, double? x, double? y, string? scale, string? display, string? configuration = null, bool? exploded = null);

    // Straight edges and circles a view shows, in sheet mm, with handles other drawing tools accept.
    IReadOnlyList<ViewEntity> ListViewEntities(string id, string view);

    // offset is in model mm from the parent view's centre; horizontal lines cut across, vertical ones top to bottom.
    ViewInfo AddSectionView(string id, string parent, bool vertical, double offset, double x, double y, string? label, bool flip);

    // at is the circle's centre in model mm from the parent view's centre.
    ViewInfo AddDetailView(string id, string parent, double atX, double atY, double radius, string scale, double x, double y, string? label);

    ViewInfo AddAuxiliaryView(string id, string parent, string edge, double x, double y, string? label);

    // Imports dimensions the model has marked for drawings, without repeating any. view null means every view.
    IReadOnlyList<ViewInfo> ImportDimensions(string id, string? view);

    IReadOnlyList<SheetDimension> ListSheetDimensions(string id, string? view);

    // A reference dimension from view_entities edges, its text at sheet mm.
    SheetDimension AddSheetDimension(string id, string view, string first, string? second, double x, double y);

    // Tolerances live on the model's dimension, so this changes the model until it's saved.
    ToleranceChange SetTolerance(string id, string dimension, string kind, double upper, double lower);

    MarkCount AddMarks(string id, string view);

    string AddNote(string id, string text, double x, double y, string? view);

    DrawingFacts ReadDrawingFacts(string id);

    // Writes the custom property the title block field reads: the drawing's, or the model's for a $PRPSHEET field.
    TitleChange SetTitleField(string id, string name, string value);

    IReadOnlyList<BomRow> AddBom(string id, string view, double x, double y, string? bomTemplate);

    IReadOnlyList<BomRow> ReadBomTable(string id);

    IReadOnlyList<BalloonInfo> AddBalloons(string id, string view);

    // On the assembly, in its active configuration: one line for each move of each exploded component.
    IReadOnlyList<ExplodeLine> AddExplodeLines(string id);

    string NewMotionStudy(string id, string? name, double seconds);

    MotorMade AddMotionMotor(string id, string study, MotionMotorSpec spec);

    void AddMotionPath(string id, string study, MotionPathSpec spec);

    double MotionStudySeconds(string id, string study);

    // Calculates the study, then samples every component against where it was at time 0. hold leaves the assembly at
    // that time, so an interference check can run on a pose partway through.
    IReadOnlyList<MotionSample> RunMotionStudy(string id, string study, IReadOnlyList<double> times, double? hold = null);

    // The motion study showing in an assembly, or null when its Model tab is.
    string? ActiveMotionStudy(string id);

    IReadOnlyList<Raster> RenderMotionStudy(string id, string study, StandardView view, int width, int height, int frames);

    // An MP4 or AVI rendered by SOLIDWORKS at width x height, with the view fitted to everything the study moves through.
    MotionVideo RecordMotionVideo(string id, string study, StandardView view, string path, int fps, int width, int height);

    // One full turn of the camera around the model's vertical axis, looking down by tilt degrees.
    IReadOnlyList<Raster> RenderTurntable(string id, double tilt, int width, int height, int frames);

    // The dimensions and notes on one view or the whole sheet, leaving out the sheet format's own text.
    IReadOnlyList<SheetAnnotation> ListAnnotations(string id, string? view);

    SheetAnnotation MoveAnnotation(string id, string name, double x, double y);

    void DeleteAnnotation(string id, string name);

    // Returns the names of the views that went, since children go with their parent.
    IReadOnlyList<string> DeleteView(string id, string view);

    // Views following the sheet's scale change with it. Returns the sheet's scale as read back.
    string SetSheetScale(string id, string scale);

    // The units dimensions show, and the sheet format's statement of them.
    void SetSheetUnits(string id, string units);

    // Moves the BOM table's top left corner to a sheet position in mm. Returns where it landed.
    SheetBox MoveBom(string id, double x, double y);

    // Replaces a note's text on a view or the sheet. Returns the text as read back.
    string EditNote(string id, string name, string text);

    ViewCapture CaptureSheet(string id, int width, int height);

    // typeOrHandle is an interface name (IFeature, or Feature) or a handle whose object's interface is listed.
    IReadOnlyList<ApiMember> DescribeMembers(string? id, string typeOrHandle, string? query);

    IReadOnlyList<ApiEntry> ApiEntries();

    void RunMacro(string path, string moduleName, string procedure);

    // sink is called on the STA thread for each event SOLIDWORKS raises while the user works.
    void Subscribe(Action<SolidWorksEvent> sink);

    IReadOnlyList<EquationInfo> ListEquations(string id);

    // configurations null means all of them.
    EquationInfo SetEquation(string id, string text, IReadOnlyList<string>? configurations);

    void DeleteEquation(string id, string left);

    // Every face of a part with a point on it, its outward normal, its wall thickness and whether it can be pulled along pull.
    IReadOnlyList<FaceProbe> ProbeFaces(string id, Point3 pull);

    // null for a part that isn't sheet metal.
    SheetBends? ReadSheetBends(string id);

    // The given features as the tool calls that would make them again, for teach_stop.
    Lesson ReadLesson(string id, IReadOnlyList<string> features);

    // Calls a member through COM late binding. A call that might change the model throws NOT_ALLOWED or
    // NOT_OWNED unless writes is Allowed, and then reports the change.
    ApiResult Invoke(string? id, ApiCall request, GatewayWrites writes);

    // Called from any thread while the dispatcher may be stuck, so it must never touch COM.
    IReadOnlyList<BlockingDialog> FindBlockingDialogs();
}

public sealed record BlockingDialog(nint Handle, string Title);
