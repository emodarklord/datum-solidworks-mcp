using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class BatchToolsTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-batch-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;

    public BatchToolsTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(5));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
        Directory.CreateDirectory(Path.Combine(_root, "Datum"));
        Directory.CreateDirectory(Path.Combine(Parts, "sub"));
        foreach (var name in new[] { "bracket.SLDPRT", "frame.SLDASM", "bracket.SLDDRW", "~$bracket.SLDPRT", "notes.txt", Path.Combine("sub", "pin.SLDPRT") })
        {
            File.WriteAllText(Path.Combine(Parts, name), "fake");
        }
    }

    private string Parts => Path.Combine(_root, "parts");

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void FilesSkipsLockFilesAndOtherTypes()
    {
        Assert.Equal(["bracket.SLDDRW", "bracket.SLDPRT", "frame.SLDASM"], BatchJobs.Files(Parts, recursive: false).Select(Path.GetFileName));
        Assert.Equal(4, BatchJobs.Files(Parts, recursive: true).Count);
    }

    [Fact]
    public async Task ExportsModelsToStepAndClosesWhatItOpened()
    {
        var report = Ok(await BatchTools.Export(_connection, _workspace, Parts, "step"));

        Assert.StartsWith($"2 files to STEP in {Path.Combine(_root, "Datum", "parts step")}\n", report, StringComparison.Ordinal);
        Assert.EndsWith("2 of 2 exported\n", report, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_root, "Datum", "parts step", "bracket.step")));
        Assert.Empty(_backend.ListDocuments());
    }

    [Fact]
    public async Task PdfComesOnlyFromDrawings()
    {
        var report = Ok(await BatchTools.Export(_connection, _workspace, Parts, "pdf", recursive: true));

        Assert.Contains("- bracket.SLDDRW: ", report, StringComparison.Ordinal);
        Assert.EndsWith("1 of 1 exported\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnExistingExportNeedsOverwrite()
    {
        Ok(await BatchTools.Export(_connection, _workspace, Parts, "step"));

        var again = Ok(await BatchTools.Export(_connection, _workspace, Parts, "step"));

        Assert.Contains("FILE_EXISTS: ", again, StringComparison.Ordinal);
        Assert.EndsWith("0 of 2 exported\n", again, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUnknownFormatIsRefused()
    {
        Assert.StartsWith("INVALID_ARGUMENT: can't export to 'obj'", Error(await BatchTools.Export(_connection, _workspace, Parts, "obj")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListsPropertiesWithoutConsent()
    {
        var report = Ok(await BatchTools.Properties(_connection, _workspace, Parts));

        Assert.Contains("- frame.SLDASM: no custom properties\n", report, StringComparison.Ordinal);
        Assert.EndsWith("3 of 3 read\n", report, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SettingPropertiesNeedsConsentThenBacksUpEachFile()
    {
        var revision = new Dictionary<string, string> { ["Revision"] = "B" };

        var refused = Error(await BatchTools.Properties(_connection, _workspace, Parts, revision));
        var report = Ok(await BatchTools.Properties(_connection, _workspace, Parts, revision, allowEdits: true));

        Assert.StartsWith("NOT_OWNED: setting properties saves 3 files", refused, StringComparison.Ordinal);
        Assert.Contains("- bracket.SLDPRT: set Revision (previous file backed up to ", report, StringComparison.Ordinal);
        Assert.EndsWith("3 of 3 changed\n", report, StringComparison.Ordinal);
    }

    private static string Ok(CallToolResult result)
    {
        Assert.True(result.IsError != true, StatusToolsTests.TextOf(result));
        return StatusToolsTests.TextOf(result);
    }

    private static string Error(CallToolResult result)
    {
        Assert.True(result.IsError);
        return StatusToolsTests.TextOf(result);
    }
}
