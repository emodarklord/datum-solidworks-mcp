using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed class BatchLiveTests : IDisposable
{
    private readonly SolidWorksConnection _connection = new(() => new ComBackend(), TimeSpan.FromSeconds(90));
    private readonly Workspace _workspace;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"datum-batch-{Guid.NewGuid():N}");

    public BatchLiveTests()
    {
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
        Directory.CreateDirectory(Path.Combine(_root, "Datum", "parts"));
    }

    private string Parts => Path.Combine(_root, "Datum", "parts");

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ExportsAFolderAndSetsARevisionOnEveryPart()
    {
        await Part("plate", 40);
        await Part("block", 20);

        var exported = Ok(await BatchTools.Export(_connection, _workspace, Parts, "step"));
        var set = Ok(await BatchTools.Properties(_connection, _workspace, Parts, new Dictionary<string, string> { ["Revision"] = "B" }, allowEdits: true));
        var listed = Ok(await BatchTools.Properties(_connection, _workspace, Parts));
        Console.WriteLine($"{exported}{set}{listed}");

        Assert.EndsWith("2 of 2 exported\n", exported, StringComparison.Ordinal);
        Assert.True(new FileInfo(Path.Combine(_root, "Datum", "parts step", "plate.step")).Length > 1000);
        Assert.EndsWith("2 of 2 changed\n", set, StringComparison.Ordinal);
        Assert.Contains("- block.SLDPRT: Revision=B\n", listed, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("- plate.SLDPRT: Revision=B\n", listed, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain((await _connection.UseAsync(c => c.RunAsync(b => b.ListDocuments()), CancellationToken.None)), d => d.Path?.StartsWith(Parts, StringComparison.OrdinalIgnoreCase) == true);
    }

    private async Task Part(string name, double width)
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = width, Height = 20 }]));
        var sketch = System.Text.RegularExpressions.Regex.Match(Ok(await SketchTools.End(_connection, _workspace)), @"feat#\d+").Value;
        Ok(await FeatureTools.Extrude(_connection, _workspace, sketch, depth: 5));
        Ok(await DocumentTools.Save(_connection, _workspace, path: Path.Combine("parts", name)));
        Ok(await DocumentTools.Close(_connection));
    }

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }
}
