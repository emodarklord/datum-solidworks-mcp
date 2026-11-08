using System.Globalization;

namespace Datum.Core;

// Text is the equation as SOLIDWORKS holds it, e.g. "Depth" = 20mm. Value is what it evaluates to in the document's
// units, named by Unit, because SOLIDWORKS doesn't say whether an equation is a length.
public sealed record EquationInfo(int Index, string Text, double Value, bool Global)
{
    public string Unit { get; init; } = "";

    public string Left => Equations.LeftOf(Text);
}

public static class Equations
{
    public static string LeftOf(string text) => text.Split('=')[0].Trim().Trim('"');

    // Equations take bare numbers in the document's units, which are often inches, so a variable always carries mm.
    public static string Variable(string name, double millimetres)
    {
        Require(name);
        return string.Create(CultureInfo.InvariantCulture, $"\"{name}\" = {millimetres}mm");
    }

    public static string Driving(string dimension, string expression)
    {
        Require(dimension);
        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new DatumException(ErrorCode.InvalidArgument, "an equation needs an expression", "Write it like \"Depth\" * 2, or 20mm");
        }

        return $"\"{dimension}\" = {expression}";
    }

    private static void Require(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Contains('"', StringComparison.Ordinal) || name.Contains('=', StringComparison.Ordinal))
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"'{name}' can't be an equation's name", "Use letters, digits and @, like Depth or D1@Boss-Extrude1");
        }
    }
}
