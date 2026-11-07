using System.Text.RegularExpressions;

namespace Datum.Core;

public enum ErrorCode
{
    NotRunning,
    Unreachable,
    Timeout,
    DialogBlocking,
    Busy,
    ComError,
    InvalidArgument,
    NotFound,
    FileExists,
    NotOwned,
    PathNotAllowed,
    UnsavedChanges,
    SolidworksError,
    FeatureFailed,
    RebuildFailed,
    CannotRestore,
    NotAllowed,
    NotInVersion,
    Unexpected,
}

public static partial class ErrorCodes
{
    public static string Name(this ErrorCode code) => WordBoundary().Replace(code.ToString(), "_$1").ToUpperInvariant();

    [GeneratedRegex("(?<=.)([A-Z])")]
    private static partial Regex WordBoundary();
}

public sealed class DatumException(ErrorCode code, string message, string? hint = null) : Exception(message)
{
    public ErrorCode Code { get; } = code;

    public string? Hint { get; } = hint;

    public override string ToString() =>
        Hint is null ? $"{Code.Name()}: {Message}" : $"{Code.Name()}: {Message}. Hint: {Hint}";
}
