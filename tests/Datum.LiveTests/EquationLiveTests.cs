using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed class EquationLiveTests : IDisposable
{
    private readonly SolidWorksConnection _connection = new(() => new ComBackend(), TimeSpan.FromSeconds(90));
    private readonly Workspace _workspace;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"datum-eq-{Guid.NewGuid():N}");

    public EquationLiveTests()
    {
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
        Directory.CreateDirectory(Path.Combine(_root, "Datum"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task OneVariableDrivesTwoConfigurations()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
            Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 40, Height = 20 }]));
            Ok(await FeatureTools.Extrude(_connection, _workspace, Regex.Match(Ok(await SketchTools.End(_connection, _workspace)), @"feat#\d+").Value, depth: 10));
            var original = Ok(await AssemblyTools.Configurations(_connection)).Split('\n')[0].Split(' ')[0].Trim('*');
            var depth = Ok(await EditTools.Dimensions(_connection)).Split('\n').First(l => l.Contains("Boss-Extrude", StringComparison.Ordinal)).Split(' ')[0];

            Ok(await EquationTools.Variable(_connection, _workspace, "Depth", 20));
            var driven = Ok(await EquationTools.Set(_connection, _workspace, depth, "\"Depth\""));
            Ok(await AssemblyTools.AddConfiguration(_connection, _workspace, "Long"));
            var longer = Ok(await EquationTools.Variable(_connection, _workspace, "Depth", 50, ["Long"]));
            var inLong = Ok(await InspectionTools.Info(_connection));
            Ok(await AssemblyTools.ActivateConfiguration(_connection, original));
            var inOriginal = Ok(await InspectionTools.Info(_connection));
            var listed = Ok(await EquationTools.List(_connection));
            Console.WriteLine($"{original}\n{driven}{longer}{inLong}{inOriginal}{listed}");

            Assert.Contains("volume 40000 mm", inLong, StringComparison.Ordinal);
            Assert.Contains("volume 16000 mm", inOriginal, StringComparison.Ordinal);
            Assert.Contains("variable \"Depth\"", listed, StringComparison.Ordinal);
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    [Fact]
    public async Task ABadEquationIsRefused()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            var refused = await EquationTools.Set(_connection, _workspace, "D9@NoSuchFeature", "\"Missing\" * 2");

            Assert.True(refused.IsError);
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
