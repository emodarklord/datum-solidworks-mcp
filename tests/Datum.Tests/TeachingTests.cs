using Datum.Core;
using Datum.Fakes;
using Datum.Mcp;
using ModelContextProtocol.Protocol;

namespace Datum.Tests;

public sealed class TeachingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("datum-teach-").FullName;
    private readonly FakeBackend _backend = new();
    private readonly SolidWorksConnection _connection;
    private readonly Workspace _workspace;
    private string _part = "";

    public TeachingTests()
    {
        _connection = new SolidWorksConnection(() => _backend, TimeSpan.FromSeconds(5));
        _workspace = new Workspace([Path.Combine(_root, "Datum")], Path.Combine(_root, "backups"));
        Directory.CreateDirectory(Path.Combine(_root, "Datum"));
    }

    public void Dispose()
    {
        _connection.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task WhatTheUserMadeBecomesARecipeStartingFromANewPart()
    {
        await Part();
        Ok(await IntentTools.TeachStart(_connection));
        var geometry = _backend.GeometryOf(_part);
        geometry.Features.Add(new FeatureInfo("feat#1", "Sketch1", "ProfileFeature", 0, false, null));
        geometry.Features.Add(new FeatureInfo("feat#2", "Boss-Extrude1", "Extrusion", 0, false, null));
        geometry.Lesson = new Lesson(
            [
                new TaughtStep("sketch_begin", new Dictionary<string, object?> { ["on"] = "Front" }),
                new TaughtStep("sketch_add", new Dictionary<string, object?> { ["entities"] = new[] { new Dictionary<string, object?> { ["type"] = "circle", ["cx"] = 0, ["cy"] = 0, ["radius"] = 5 } } }),
                new TaughtStep("sketch_end", new Dictionary<string, object?>(), "feat#1"),
                new TaughtStep("feature_extrude", new Dictionary<string, object?> { ["sketch"] = "feat#1", ["depth"] = 8 }, "feat#2"),
            ],
            ["Shell1 (Shell)"],
            [],
            []);

        var reply = Ok(await IntentTools.TeachStop(_connection, _workspace, "puck"));
        var steps = Recipes.Read(Path.Combine(_root, "Datum", "puck.intent.json"));

        Assert.StartsWith("Wrote 5 steps to ", reply, StringComparison.Ordinal);
        Assert.Contains("recipe_run puck replays it\nCouldn't learn: Shell1 (Shell)", reply, StringComparison.Ordinal);
        Assert.Equal(["doc_new", "sketch_begin", "sketch_add", "sketch_end", "feature_extrude"], steps.Select(s => s.Tool));
        Assert.Equal("feat#1", steps[4].Args["sketch"].GetString());
    }

    [Fact]
    public async Task StopWithoutStartSaysWhatToDo()
    {
        await Part();

        Assert.StartsWith("INVALID_ARGUMENT: Part1 isn't being watched. Hint: Call teach_start", Error(await IntentTools.TeachStop(_connection, _workspace, "x")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NothingLearnableIsAnError()
    {
        await Part();
        Ok(await IntentTools.TeachStart(_connection));
        _backend.GeometryOf(_part).Lesson = new Lesson([], ["Shell1 (Shell)"], [], []);

        Assert.StartsWith("NOT_FOUND: nothing Datum can learn since teach_start; it can't learn Shell1 (Shell)", Error(await IntentTools.TeachStop(_connection, _workspace, "x")), StringComparison.Ordinal);
    }

    private async Task Part()
    {
        Ok(await DocumentTools.New(_connection, _workspace, "part"));
        _part = _backend.ListDocuments().Single(d => d.Active).Id;
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
