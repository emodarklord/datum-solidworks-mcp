namespace Datum.Core;

public static class BatchJobs
{
    public const int MostFiles = 200;

    private static readonly string[] DrawingFormats = [".pdf", ".dxf", ".dwg"];
    private static readonly string[] ModelFormats = [".step", ".stp", ".iges", ".igs", ".stl", ".x_t", ".x_b"];

    // SOLIDWORKS files in a folder, skipping the ~$ lock files it leaves beside open documents.
    public static IReadOnlyList<string> Files(string folder, bool recursive)
    {
        if (!Directory.Exists(folder))
        {
            throw new DatumException(ErrorCode.NotFound, $"no folder at '{folder}'", "Pass the full path of a folder of SOLIDWORKS files");
        }

        return [.. Directory.EnumerateFiles(folder, "*", recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
            .Where(f => DocumentTypes.FromPath(f) is not null && !Path.GetFileName(f).StartsWith("~$", StringComparison.Ordinal))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    public static string Extension(string format)
    {
        var extension = "." + format.Trim().TrimStart('.').ToLowerInvariant();
        return DrawingFormats.Contains(extension) || ModelFormats.Contains(extension)
            ? extension
            : throw new DatumException(ErrorCode.InvalidArgument, $"can't export to '{format}'", $"Use one of {string.Join(" ", DrawingFormats.Concat(ModelFormats))}");
    }

    // PDF, DXF and DWG come from drawings; the 3D formats from parts and assemblies.
    public static bool Exports(DocumentType type, string extension) =>
        DrawingFormats.Contains(extension) ? type == DocumentType.Drawing : type != DocumentType.Drawing;
}
