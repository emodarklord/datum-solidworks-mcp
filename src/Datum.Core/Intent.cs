using System.Text.Json;
using System.Text.RegularExpressions;

namespace Datum.Core;

// One modelling step as it was asked for. Tag is neutral for steps any CAD could run and solidworks for gateway calls.
// Refs describes each face or edge handle in Args by its geometry, since handles change between sessions. A face is
// "face:<key>", an edge "edge:<curve>|<face key>|<face key>".
public sealed record IntentStep(string Tool, Dictionary<string, JsonElement> Args, string? Made, string Tag)
{
    public Dictionary<string, string> Refs { get; init; } = [];
}

public sealed partial class IntentLog
{
    private static readonly string[] Prefixes = ["toolset_enable", "doc_new", "sketch_begin", "sketch_add", "sketch_end", "feature_extrude", "feature_revolve", "feature_sweep", "feature_loft", "feature_rib", "feature_fillet", "feature_chamfer", "feature_hole", "feature_pattern", "feature_mirror", "feature_shell", "plane_create", "axis_create", "dimension_set", "material_set", "sheet_base", "sheet_bend", "sheet_hem", "weldment_member", "weldment_trim", "invoke"];

    private readonly List<IntentStep> _steps = [];

    public IReadOnlyList<IntentStep> Steps => _steps;

    public static bool Records(string tool) => Prefixes.Contains(tool, StringComparer.Ordinal);

    public void Add(string tool, IReadOnlyDictionary<string, JsonElement>? args, string reply, Dictionary<string, string>? refs = null)
    {
        var copied = (args ?? new Dictionary<string, JsonElement>()).ToDictionary(pair => pair.Key, pair => pair.Value.Clone());
        _steps.Add(new IntentStep(tool, copied, FeatureHandle().Match(reply) is { Success: true } made ? made.Value : null, tool == "invoke" ? "solidworks" : "neutral") { Refs = refs ?? [] });
    }

    public static IReadOnlyList<string> LooseHandles(string text) => [.. LooseHandle().Matches(text).Select(m => m.Value).Distinct()];

    public string Export() => JsonSerializer.Serialize(new { version = 0, steps = _steps }, JsonSerializerOptions.Web);

    public static IReadOnlyList<IntentStep> Import(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.GetProperty("steps").Deserialize<List<IntentStep>>(JsonSerializerOptions.Web) ?? [];
    }

    // Runs the steps through call, which returns a tool's reply. Feature handles map to the new ones made on replay, and
    // faces and edges are found again by their geometry through model_faces.
    public static async Task ReplayAsync(IReadOnlyList<IntentStep> steps, Func<string, Dictionary<string, JsonElement>, Task<string>> call)
    {
        var handles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var step in steps)
        {
            var text = JsonSerializer.Serialize(step.Args);
            var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var loose in LooseHandles(text))
            {
                found[loose] = step.Refs.TryGetValue(loose, out var described)
                    ? await FindAsync(described, call).ConfigureAwait(false)
                    : throw new DatumException(ErrorCode.InvalidArgument, $"{step.Tool} refers to {loose} with no recorded geometry", "Record the session again with this version of Datum");
            }

            var mapped = LooseHandle().Replace(FeatureHandle().Replace(text, match => handles.GetValueOrDefault(match.Value, match.Value)), match => found.GetValueOrDefault(match.Value, match.Value));
            var reply = await call(step.Tool, JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(mapped)!).ConfigureAwait(false);
            if (step.Made is not null && FeatureHandle().Match(reply) is { Success: true } made)
            {
                handles[step.Made] = made.Value;
            }
        }
    }

    private static async Task<string> FindAsync(string described, Func<string, Dictionary<string, JsonElement>, Task<string>> call)
    {
        static Dictionary<string, JsonElement> Query(string kind) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>($"{{\"kind\":\"{kind}\",\"limit\":1000}}")!;
        var faces = (await call("model_faces", Query("face")).ConfigureAwait(false)).Split('\n')
            .Select(line => line.Split(' ', 2)).Where(parts => parts.Length == 2 && parts[0].StartsWith("face#", StringComparison.Ordinal))
            .ToDictionary(parts => parts[0], parts => parts[1]);
        if (described.StartsWith("face:", StringComparison.Ordinal))
        {
            return faces.FirstOrDefault(face => face.Value == described[5..]).Key ?? throw Missing(described);
        }

        var (curve, sides) = (described[5..].Split('|')[0], described[5..].Split('|')[1..].Order(StringComparer.Ordinal).ToList());
        foreach (var line in (await call("model_faces", Query("edge")).ConfigureAwait(false)).Split('\n').Where(l => l.StartsWith("edge#", StringComparison.Ordinal)))
        {
            var at = line.IndexOf(" faces ", StringComparison.Ordinal);
            var adjoining = at < 0 ? [] : line[(at + 7)..].Split(',').Select(face => faces.GetValueOrDefault(face.Trim(), "")).Order(StringComparer.Ordinal).ToList();
            if (line.Split(' ', 2)[1].StartsWith(curve, StringComparison.Ordinal) && adjoining.SequenceEqual(sides))
            {
                return line.Split(' ')[0];
            }
        }

        throw Missing(described);
    }

    private static DatumException Missing(string described) =>
        new(ErrorCode.NotFound, $"no {described[..4]} like the recorded one ({described[5..]})", "The part came out differently on replay from this step on");

    [GeneratedRegex(@"feat#\d+")]
    private static partial Regex FeatureHandle();

    [GeneratedRegex(@"(face|edge)#\d+")]
    private static partial Regex LooseHandle();
}
