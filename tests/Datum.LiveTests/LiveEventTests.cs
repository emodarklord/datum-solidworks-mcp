using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed class LiveEventTests : IDisposable
{
    private readonly SolidWorksConnection _connection = new(() => new ComBackend(), TimeSpan.FromSeconds(90));
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"datum-events-{Guid.NewGuid():N}");

    public void Dispose()
    {
        _connection.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public async Task NewRebuildSelectAndSaveAreHeard()
    {
        var workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
        Directory.CreateDirectory(Path.Combine(_root, "Datum"));
        Ok(await DocumentTools.New(_connection, workspace, "part"));
        try
        {
            Ok(await SketchTools.Begin(_connection, workspace, "Front"));
            Ok(await SketchTools.Add(_connection, workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 40, Height = 20 }]));
            Ok(await FeatureTools.Extrude(_connection, workspace, System.Text.RegularExpressions.Regex.Match(Ok(await SketchTools.End(_connection, workspace)), @"feat#\d+").Value, depth: 10));
            Ok(await InspectionTools.Select(_connection, [Ok(await InspectionTools.Faces(_connection, normal: "+z", limit: 1)).Split(' ')[0]]));
            Ok(await DocumentTools.Save(_connection, workspace, path: Path.Combine(_root, "Datum", "heard.sldprt")));
            await Task.Delay(500);
            var heard = Ok(await LiveTools.Recent(_connection, 50));
            Console.WriteLine(heard);

            Assert.Contains(" new ", heard, StringComparison.Ordinal);
            Assert.Contains(" rebuild ", heard, StringComparison.Ordinal);
            Assert.Contains(" save ", heard, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }
}
