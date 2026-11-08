using System.Text.Json;
using Datum.Core;

namespace Datum.Mcp;

// "selected" in place of a handle means what the user has selected in SOLIDWORKS. Single handles take the selection in
// order, so mate_add with first and second both "selected" mates the two picked faces.
internal static class Selected
{
    public const string Word = "selected";

    public static bool Mentioned(IDictionary<string, JsonElement>? args) =>
        args?.Values.Any(value => IsWord(value) || (value.ValueKind == JsonValueKind.Array && value.EnumerateArray().Any(IsWord))) == true;

    public static async Task<Dictionary<string, JsonElement>> ExpandAsync(SolidWorksConnection solidWorks, IDictionary<string, JsonElement> args, CancellationToken cancellationToken)
    {
        var handles = await solidWorks.UseAsync(
            async client =>
            {
                var active = (await client.RunAsync(b => b.ListDocuments(), cancellationToken: cancellationToken).ConfigureAwait(false)).FirstOrDefault(d => d.Active)
                    ?? throw new DatumException(ErrorCode.NotFound, "no document is open", "Open one in SOLIDWORKS and select something");
                var selection = await client.RunAsync(b => b.ReadSelection(active.Id), cancellationToken: cancellationToken).ConfigureAwait(false);
                return selection.Select(item => item.Handle).OfType<string>().ToList();
            },
            cancellationToken).ConfigureAwait(false);
        if (handles.Count == 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, "nothing is selected in SOLIDWORKS", "Select the faces or edges first, or pass their handles");
        }

        var next = 0;
        string Take() => next < handles.Count ? handles[next++] : throw new DatumException(ErrorCode.InvalidArgument, $"only {handles.Count} things are selected", "Select one more, or pass the handle");
        return args.ToDictionary(
            pair => pair.Key,
            pair => IsWord(pair.Value) ? JsonSerializer.SerializeToElement(Take())
                : pair.Value.ValueKind == JsonValueKind.Array && pair.Value.EnumerateArray().Any(IsWord) ? JsonSerializer.SerializeToElement(handles)
                : pair.Value);
    }

    private static bool IsWord(JsonElement value) => value.ValueKind == JsonValueKind.String && string.Equals(value.GetString(), Word, StringComparison.OrdinalIgnoreCase);
}
