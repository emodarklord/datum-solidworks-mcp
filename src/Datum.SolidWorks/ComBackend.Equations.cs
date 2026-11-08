using Datum.Core;
using SolidWorks.Interop.sldworks;
using SolidWorks.Interop.swconst;

namespace Datum.SolidWorks;

public sealed partial class ComBackend
{
    public IReadOnlyList<EquationInfo> ListEquations(string id) => Call(() => EquationsOf(Find(id)));

    public EquationInfo SetEquation(string id, string text, IReadOnlyList<string>? configurations) => Call(() =>
    {
        var document = Find(id);
        var equations = document.GetEquationMgr();
        var existing = EquationsOf(document).FirstOrDefault(e => string.Equals(e.Left, Equations.LeftOf(text), StringComparison.OrdinalIgnoreCase));
        var option = configurations is { Count: > 0 } ? swInConfigurationOpts_e.swSpecifyConfiguration : swInConfigurationOpts_e.swAllConfiguration;
        object? names = configurations is { Count: > 0 } ? configurations.ToArray() : null;
        var errorsBefore = RebuildErrors(document);
        var index = existing is null
            ? (names is null ? equations.Add2(-1, text, true) : equations.Add3(equations.GetCount(), text, true, (int)option, names))
            : equations.SetEquationAndConfigurationOption(existing.Index, text, (int)option, names) == 0 ? existing.Index : -1;
        if (index < 0)
        {
            throw new DatumException(ErrorCode.InvalidArgument, $"SOLIDWORKS didn't accept {text}", "Check the names: a dimension is like D1@Boss-Extrude1 from model_dimensions, a variable is in quotes, and numbers carry units like 20mm");
        }

        equations.EvaluateAll();
        document.EditRebuild3();
        if (RebuildErrors(document).Except(errorsBefore).ToList() is { Count: > 0 } broken)
        {
            if (existing is null)
            {
                equations.Delete(index);
            }
            else
            {
                equations.SetEquationAndConfigurationOption(existing.Index, existing.Text, (int)swInConfigurationOpts_e.swAllConfiguration, null);
            }

            equations.EvaluateAll();
            document.EditRebuild3();
            throw new DatumException(ErrorCode.RebuildFailed, $"{text} breaks the rebuild: {string.Join(", ", broken)}", "It was taken back; try a value the features can accept");
        }

        return EquationsOf(document).First(e => string.Equals(e.Left, Equations.LeftOf(text), StringComparison.OrdinalIgnoreCase));
    });

    public void DeleteEquation(string id, string left) => Call(() =>
    {
        var document = Find(id);
        var found = EquationsOf(document).FirstOrDefault(e => string.Equals(e.Left, left, StringComparison.OrdinalIgnoreCase))
            ?? throw new DatumException(ErrorCode.NotFound, $"no equation for '{left}'", "equation_list shows them");
        document.GetEquationMgr().Delete(found.Index);
        document.EditRebuild3();
        return true;
    });

    private static List<EquationInfo> EquationsOf(IModelDoc2 document)
    {
        var equations = document.GetEquationMgr();
        var unit = ((swLengthUnit_e)document.GetUserPreferenceIntegerValue((int)swUserPreferenceIntegerValue_e.swUnitsLinear)).ToString().Replace("sw", "", StringComparison.Ordinal).ToLowerInvariant();
        return [.. Enumerable.Range(0, equations.GetCount()).Select(i => new EquationInfo(i, equations.Equation[i], equations.Value[i], equations.GlobalVariable[i]) { Unit = unit })];
    }
}
