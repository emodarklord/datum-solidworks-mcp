using System.Text.Json;

namespace Datum.Core;

// A recipe is a session's steps saved by intent_export as <name>.intent.json in the Datum folder.
public static class Recipes
{
    public const string Extension = ".intent.json";
    private const int MostListed = 100;

    // The Datum folder can be a big one like Temp, so only two levels down and nothing that refuses access.
    private static readonly EnumerationOptions Search = new() { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true };

    public static IReadOnlyList<string> Find(string folder) =>
        Directory.Exists(folder)
            ? [.. Directory.EnumerateFiles(folder, "*" + Extension, Search).Order(StringComparer.OrdinalIgnoreCase).Take(MostListed)]
            : [];

    public static string NameOf(string file) => Path.GetFileName(file)[..^Extension.Length];

    public static string PathOf(string folder, string name)
    {
        var bare = name.EndsWith(Extension, StringComparison.OrdinalIgnoreCase) ? name[..^Extension.Length] : name;
        return Find(folder).FirstOrDefault(f => string.Equals(NameOf(f), Path.GetFileName(bare), StringComparison.OrdinalIgnoreCase))
            ?? throw new DatumException(ErrorCode.NotFound, $"no recipe '{bare}' in {folder}", "Save one with intent_export as <name>.intent.json, or read datum://recipes for the list");
    }

    public static IReadOnlyList<IntentStep> Read(string file)
    {
        try
        {
            return IntentLog.Import(File.ReadAllText(file));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"'{file}' isn't a recipe Datum can read: {e.Message}", "Write it again with intent_export");
        }
    }

    public static string Summary(string file, IReadOnlyList<IntentStep> steps) =>
        $"{NameOf(file)}: {steps.Count} steps ({string.Join(", ", steps.Select(s => s.Tool).Distinct())})";

    public static string Describe(string file, IReadOnlyList<IntentStep> steps) =>
        $"{Summary(file, steps)}\n" + string.Concat(steps.Select((s, i) =>
            $"{i + 1}. {s.Tool} {JsonSerializer.Serialize(s.Args)}{(s.Made is null ? "" : $" → {s.Made}")}\n"));
}
