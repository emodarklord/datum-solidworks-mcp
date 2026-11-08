using System.ComponentModel;
using System.Globalization;
using System.Text;
using Datum.Core;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

[McpServerToolType]
public static class BatchTools
{
    private const string Toolset = "batch";

    private static readonly TimeSpan FileTimeout = TimeSpan.FromMinutes(2);

    [Toolset(Toolset)]
    [McpServerTool(Name = "batch_export", Destructive = false, OpenWorld = false)]
    [Description("Export every SOLIDWORKS file in a folder: drawings to PDF, DXF or DWG, parts and assemblies to STEP, IGES, STL or Parasolid. Files are opened read-only and closed again; the exports go in a folder inside the Datum folder.")]
    public static Task<CallToolResult> Export(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Folder of .SLDPRT, .SLDASM or .SLDDRW files")] string folder,
        [Description("pdf, dxf, dwg, step, iges, stl or x_t")] string format,
        [Description("Output folder; relative paths go in the Datum folder. Defaults to '<folder name> <format>'")] string? @out = null,
        [Description("Include subfolders")] bool recursive = false,
        [Description("Replace existing exports (each is backed up first)")] bool overwrite = false,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var source = workspace.ResolveReadPath(folder);
                var extension = BatchJobs.Extension(format);
                var files = BatchJobs.Files(source, recursive).Where(f => BatchJobs.Exports(DocumentTypes.FromPath(f)!.Value, extension)).ToList();
                var target = workspace.ResolveWritePath(@out ?? $"{Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))} {extension.TrimStart('.')}");
                var report = new StringBuilder($"{files.Count} files to {extension.TrimStart('.').ToUpperInvariant()} in {target}\n");
                if (files.Count == 0)
                {
                    return report.Append("Nothing to export: PDF, DXF and DWG come from drawings, the other formats from parts and assemblies\n").ToString();
                }

                Directory.CreateDirectory(target);
                var done = 0;
                foreach (var file in files.Take(BatchJobs.MostFiles))
                {
                    var name = Path.GetRelativePath(source, file);
                    var line = await EachAsync(client, file, async document =>
                    {
                        var full = workspace.ResolveWritePath(Path.Combine(target, Path.ChangeExtension(name, extension)));
                        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                        var backup = DocumentTools.Replace(workspace, full, overwrite);
                        await client.RunAsync(b => { b.ExportDocument(document.Id, full); return true; }, FileTimeout, cancellationToken).ConfigureAwait(false);
                        done++;
                        return $"{new FileInfo(full).Length / 1024} KB{DocumentTools.BackupNote(backup)}";
                    }, readOnly: true, cancellationToken).ConfigureAwait(false);
                    report.Append(CultureInfo.InvariantCulture, $"- {name}: {line}\n");
                }

                return Finish(report, done, files.Count, "exported");
            },
            cancellationToken));

    [Toolset(Toolset)]
    [McpServerTool(Name = "batch_properties", Destructive = true, OpenWorld = false)]
    [Description("List the custom properties of every SOLIDWORKS file in a folder, or set some on all of them. Title blocks show these properties, so this also fills title blocks. Setting saves the user's files: ask first and pass allow_edits; each file is backed up before it changes.")]
    public static Task<CallToolResult> Properties(
        SolidWorksConnection solidWorks,
        Workspace workspace,
        [Description("Folder of .SLDPRT, .SLDASM or .SLDDRW files")] string folder,
        [Description("Properties to set on every file, e.g. {\"Revision\": \"B\", \"DrawnBy\": \"AK\"}; leave out to only list")] Dictionary<string, string>? set = null,
        [Description("The user agreed to these files being changed and saved")] bool allowEdits = false,
        [Description("Include subfolders")] bool recursive = false,
        CancellationToken cancellationToken = default) =>
        ToolErrors.Run(() => solidWorks.UseAsync(
            async client =>
            {
                var source = workspace.ResolveReadPath(folder);
                var files = BatchJobs.Files(source, recursive);
                var writing = set is { Count: > 0 };
                if (writing && !allowEdits)
                {
                    throw new DatumException(ErrorCode.NotOwned, $"setting properties saves {files.Count} files Datum didn't create", "Ask the user, then pass allow_edits: true; each file is backed up first");
                }

                var report = new StringBuilder($"{files.Count} files in {source}\n");
                var done = 0;
                foreach (var file in files.Take(BatchJobs.MostFiles))
                {
                    var line = await EachAsync(client, file, async document =>
                    {
                        if (!writing)
                        {
                            var properties = await client.RunAsync(b => b.ListProperties(document.Id, ""), FileTimeout, cancellationToken).ConfigureAwait(false);
                            done++;
                            return properties.Count == 0 ? "no custom properties" : string.Join("; ", properties.Select(p => $"{p.Name}={p.Value}"));
                        }

                        if (document.Modified)
                        {
                            throw new DatumException(ErrorCode.UnsavedChanges, "has unsaved changes in SOLIDWORKS, so it was left alone");
                        }

                        var backup = workspace.BackUp(file);
                        foreach (var (name, value) in set!)
                        {
                            await client.RunAsync(b => b.SetProperty(document.Id, name, value, ""), FileTimeout, cancellationToken).ConfigureAwait(false);
                        }

                        await client.RunAsync(b => b.SaveDocument(document.Id, null), FileTimeout, cancellationToken).ConfigureAwait(false);
                        done++;
                        return $"set {string.Join(", ", set!.Keys)}{DocumentTools.BackupNote(backup)}";
                    }, readOnly: !writing, cancellationToken).ConfigureAwait(false);
                    report.Append(CultureInfo.InvariantCulture, $"- {Path.GetRelativePath(source, file)}: {line}\n");
                }

                return Finish(report, done, files.Count, writing ? "changed" : "read");
            },
            cancellationToken));

    // Opens the file unless it already is, runs the job, and closes only what it opened, so the user's open
    // documents stay as they were. A failure is reported on the file's line and the batch goes on.
    private static async Task<string> EachAsync(SolidWorksClient client, string file, Func<DocumentInfo, Task<string>> job, bool readOnly, CancellationToken cancellationToken)
    {
        DocumentInfo? opened = null;
        try
        {
            var open = (await client.RunAsync(b => b.ListDocuments(), FileTimeout, cancellationToken).ConfigureAwait(false))
                .FirstOrDefault(d => string.Equals(d.Path, file, StringComparison.OrdinalIgnoreCase));
            if (open is null)
            {
                opened = await client.RunAsync(b => b.OpenDocument(file, readOnly), FileTimeout, cancellationToken).ConfigureAwait(false);
            }

            return await job(open ?? opened!).ConfigureAwait(false);
        }
        catch (DatumException e)
        {
            return e.ToString();
        }
        finally
        {
            if (opened is not null)
            {
                await client.RunAsync(b => { b.CloseDocument(opened.Id); return true; }, FileTimeout, CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private static string Finish(StringBuilder report, int done, int total, string verb)
    {
        if (total > BatchJobs.MostFiles)
        {
            report.Append(CultureInfo.InvariantCulture, $"Stopped after {BatchJobs.MostFiles} files; {total - BatchJobs.MostFiles} more not {verb}. Run it again on a subfolder\n");
        }

        return report.Append(CultureInfo.InvariantCulture, $"{done} of {Math.Min(total, BatchJobs.MostFiles)} {verb}\n").ToString();
    }
}
