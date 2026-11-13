using System.Text.Json;
using Datum.Core;

namespace Datum.Mcp;

// The SDK answers arguments it can't bind with a bare "An error occurred invoking", before Datum's own checks run. This
// says what was wrong from the tool's input schema instead.
public static class ArgumentErrors
{
    public static DatumException Explain(string tool, JsonElement schema, IReadOnlyCollection<string> given)
    {
        var properties = schema.TryGetProperty("properties", out var listed) ? listed.EnumerateObject().Select(p => p.Name).ToList() : [];
        var required = schema.TryGetProperty("required", out var needed) ? needed.EnumerateArray().Select(r => r.GetString()!).ToList() : [];
        var unknown = given.Where(name => !properties.Contains(name, StringComparer.Ordinal)).ToList();
        var missing = required.Where(name => !given.Contains(name, StringComparer.Ordinal)).ToList();
        var problems = new List<string>();
        if (unknown.Count > 0)
        {
            problems.Add($"it doesn't take {string.Join(", ", unknown)}");
        }

        if (missing.Count > 0)
        {
            problems.Add($"it needs {string.Join(", ", missing)}");
        }

        var takes = string.Join(", ", properties.Select(p => required.Contains(p) ? $"{p} (required)" : p));
        return new DatumException(
            ErrorCode.InvalidArgument,
            problems.Count > 0 ? $"{tool} couldn't take these arguments: {string.Join("; ", problems)}" : $"{tool} couldn't read one of its arguments, probably a value of the wrong type",
            $"It takes {takes}");
    }
}
