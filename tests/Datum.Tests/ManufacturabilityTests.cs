using Datum.Core;

namespace Datum.Tests;

public sealed class ManufacturabilityTests
{
    private static readonly Point3 Up = new(0, 0, 1);

    private static FaceProbe Face(string handle, Point3 normal, double? thickness = 10, bool blocked = false) =>
        new(handle, SurfaceKind.Plane, default, normal, thickness, blocked);

    private static MakeLimits Limits(string process) => new(process, Up, 1, 1);

    [Fact]
    public void AThinWallIsFlaggedForAnyProcess()
    {
        Assert.Contains("face#1: wall 0.5 mm, under 1 mm", Manufacturability.Find([Face("face#1", Up, 0.5)], null, Limits("machining")));
    }

    [Fact]
    public void AStraightSideHasNoDraftForMolding()
    {
        var problems = Manufacturability.Find([Face("face#2", new Point3(1, 0, 0))], null, Limits("molding"));

        Assert.Contains("face#2: draft 0.0°, under 1°", problems);
    }

    [Fact]
    public void ATaperedSidePassesAndMachiningIgnoresDraft()
    {
        var tapered = Face("face#3", new Point3(Math.Cos(Math.PI / 18), 0, Math.Sin(Math.PI / 18)));

        Assert.Empty(Manufacturability.Find([tapered], null, Limits("molding")));
        Assert.Empty(Manufacturability.Find([Face("face#2", new Point3(1, 0, 0))], null, Limits("machining")));
    }

    [Fact]
    public void ABlockedFaceIsAnUndercut()
    {
        Assert.Contains("face#4: undercut, the part is in the way of pulling it out", Manufacturability.Find([Face("face#4", Up, blocked: true)], null, Limits("molding")));
    }

    [Fact]
    public void ABendTighterThanTheSheetIsFlagged()
    {
        var sheet = new SheetBends(2, [("Bend1", 1), ("Bend2", 3)]);

        var problem = Assert.Single(Manufacturability.Find([], sheet, Limits("sheetmetal")));

        Assert.StartsWith("Bend1: bend radius 1 mm", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownProcessIsRefused()
    {
        Assert.Throws<DatumException>(() => Manufacturability.Validate(Limits("welding")));
    }
}
