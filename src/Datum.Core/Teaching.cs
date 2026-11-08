namespace Datum.Core;

// A feature the user made by hand, as the Datum tool call that would make it again. Made is the feature's handle,
// so later steps that refer to it can be mapped on replay.
public sealed record TaughtStep(string Tool, IReadOnlyDictionary<string, object?> Args, string? Made = null);

// Faces and Edges describe the handles in Steps as they were when each feature used them.
public sealed record Lesson(IReadOnlyList<TaughtStep> Steps, IReadOnlyList<string> Skipped, IReadOnlyList<FaceInfo> Faces, IReadOnlyList<EdgeInfo> Edges);

// What a document had when teach_start was called. FromScratch means it had no solid yet, so the recipe starts with
// doc_new.
public sealed record TeachingStart(IReadOnlySet<string> Features, bool FromScratch);
