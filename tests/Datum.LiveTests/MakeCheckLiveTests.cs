using System.Text.RegularExpressions;
using Datum.Core;
using Datum.Mcp;
using Datum.SolidWorks;
using ModelContextProtocol.Protocol;

namespace Datum.LiveTests;

public sealed class MakeCheckLiveTests : IDisposable
{
    private readonly SolidWorksConnection _connection = new(() => new ComBackend(), TimeSpan.FromSeconds(90));
    private readonly Workspace _workspace;
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"datum-make-{Guid.NewGuid():N}");

    public MakeCheckLiveTests()
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
    public async Task AThickBlockMachinesAndAThinPocketFloorDoesnt() => await InNewPart(async () =>
    {
        await Block(50, 30, 10);
        var good = Ok(await CheckTools.MakeCheck(_connection));
        Ok(await SketchTools.Begin(_connection, _workspace, Ok(await InspectionTools.Faces(_connection, normal: "+z", limit: 1)).Split(' ')[0]));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = 30, Height = 15 }]));
        Ok(await FeatureTools.Extrude(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), depth: 9.5, cut: true));
        var bad = Ok(await CheckTools.MakeCheck(_connection));
        Console.WriteLine($"{good}{bad}");

        Assert.Contains("No problems found", good, StringComparison.Ordinal);
        Assert.Contains("wall 0.5 mm", bad, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AStraightBlockHasNoDraftForMoldingButAFrustumDoes() => await InNewPart(async () =>
    {
        await Block(50, 30, 10);
        var straight = Ok(await CheckTools.MakeCheck(_connection, "molding"));
        Console.WriteLine(straight);

        Assert.Contains("draft 0.0°", straight, StringComparison.Ordinal);
    });

    [Fact]
    public async Task AFrustumPullsCleanly() => await InNewPart(async () =>
    {
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace,
        [
            new SketchEntitySpec { Type = "polyline", Closed = true, Points = [[0, 0], [30, 0], [15, 50], [0, 50]] },
            new SketchEntitySpec { Type = "centerline", X1 = 0, Y1 = -10, X2 = 0, Y2 = 60 },
        ]));
        Ok(await FeatureTools.Revolve(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace)))));
        var frustum = Ok(await CheckTools.MakeCheck(_connection, "molding", pull: "+y"));
        Console.WriteLine(frustum);

        Assert.Contains("No problems found", frustum, StringComparison.Ordinal);
    });

    [Fact]
    public async Task ASideHoleIsAnUndercut() => await InNewPart(async () =>
    {
        await Block(50, 30, 20);
        Ok(await SketchTools.Begin(_connection, _workspace, "Right"));

        // The Right plane's sketch x runs along model -z, so this hole goes through the middle of the block's depth.
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "circle", Cx = -10, Cy = 0, Radius = 4 }]));
        Ok(await FeatureTools.Extrude(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), cut: true, end: "through_all"));
        var holed = Ok(await CheckTools.MakeCheck(_connection, "molding", minDraft: 0));
        Console.WriteLine(holed);

        Assert.Contains("undercut", holed, StringComparison.Ordinal);
    });

    private async Task Block(double width, double height, double depth)
    {
        Ok(await SketchTools.Begin(_connection, _workspace, "Front"));
        Ok(await SketchTools.Add(_connection, _workspace, [new SketchEntitySpec { Type = "center_rect", Cx = 0, Cy = 0, Width = width, Height = height }]));
        Ok(await FeatureTools.Extrude(_connection, _workspace, Feat(Ok(await SketchTools.End(_connection, _workspace))), depth: depth));
    }

    private async Task InNewPart(Func<Task> test)
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        try
        {
            await test();
        }
        finally
        {
            await DocumentTools.Close(_connection, discardChanges: true);
        }
    }

    private static string Feat(string reply) => Regex.Match(reply, @"feat#\d+").Value;

    private static string Ok(CallToolResult result)
    {
        var text = Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;
        Assert.True(result.IsError != true, text);
        return text;
    }
}
