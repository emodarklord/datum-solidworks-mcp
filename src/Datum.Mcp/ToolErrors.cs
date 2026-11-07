using Datum.Core;
using ModelContextProtocol.Protocol;

namespace Datum.Mcp;

internal static class ToolErrors
{
    // Returned rather than thrown: the SDK replaces anything else with "An error occurred invoking", which tells the
    // model nothing, and prefixes the rest with its own text, which would push the error code out of the first word.
    public static async Task<CallToolResult> Run(Func<Task<string>> body)
    {
        try
        {
            return Text(await body().ConfigureAwait(false), isError: false);
        }
        catch (DatumException e)
        {
            return Text(e.ToString(), isError: true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Text(Unexpected(e), isError: true);
        }
    }

    public static async Task<CallToolResult> Run(Func<Task<IList<ContentBlock>>> body)
    {
        try
        {
            return new() { Content = await body().ConfigureAwait(false), IsError = false };
        }
        catch (DatumException e)
        {
            return Text(e.ToString(), isError: true);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return Text(Unexpected(e), isError: true);
        }
    }

    private static string Unexpected(Exception e) =>
        new DatumException(ErrorCode.Unexpected, $"{e.GetType().Name}: {e.Message}", "Retry once. If it fails again, report it with the call that caused it").ToString();

    private static CallToolResult Text(string text, bool isError) =>
        new() { Content = [new TextContentBlock { Text = text }], IsError = isError };
}
