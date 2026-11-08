using Datum.Core;
using ModelContextProtocol.Server;

namespace Datum.Mcp;

public sealed record CatalogEntry(string Name, string Toolset, string Description, string Status, bool Unavailable);

// Owns the server's tool list: keeps every registered tool aside and lists only core tools,
// enabled toolsets and tools the connected SOLIDWORKS can run. Changes reach the client as
// notifications/tools/list_changed.
public sealed class ToolCatalog(bool allTools, bool readOnly = false)
{
    // Change only what SOLIDWORKS has open, never a model or file; invoke refuses writes by itself in read-only mode.
    private static readonly HashSet<string> SessionOnly = ["toolset_enable", "doc_open", "doc_activate", "model_select", "invoke"];
    private const string ReadOnlyReason = "Datum runs read-only";

    public const string Core = "core";
    private const string Listed = "listed";

    private readonly Lock _lock = new();
    private readonly HashSet<string> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private McpServerPrimitiveCollection<McpServerTool>? _listed;
    private McpServerTool[] _all = [];
    private SolidWorksCapabilities? _capabilities;

    public IReadOnlyList<string> Toolsets
    {
        get
        {
            lock (_lock)
            {
                return [.. _all.Select(ToolsetOf).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)];
            }
        }
    }

    public void Attach(McpServerPrimitiveCollection<McpServerTool> listed)
    {
        lock (_lock)
        {
            _listed = listed;
            _all = listed.ToArray();
            Refresh();
        }
    }

    public IReadOnlyList<CatalogEntry> Entries()
    {
        lock (_lock)
        {
            return [.. _all.Select(Describe)];
        }
    }

    public IReadOnlyList<CatalogEntry> Enable(string toolset)
    {
        lock (_lock)
        {
            if (!_all.Any(t => string.Equals(ToolsetOf(t), toolset, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DatumException(
                    ErrorCode.InvalidArgument,
                    $"no toolset '{toolset}'",
                    $"known toolsets: {string.Join(", ", _all.Select(ToolsetOf).Where(t => t != Core).Distinct().Order())}");
            }

            _enabled.Add(toolset);
            Refresh();
            return [.. _all.Where(t => string.Equals(ToolsetOf(t), toolset, StringComparison.OrdinalIgnoreCase)).Select(Describe)];
        }
    }

    public void Update(SolidWorksCapabilities? capabilities)
    {
        lock (_lock)
        {
            _capabilities = capabilities;
            Refresh();
        }
    }

    public DatumException? ReadOnlyRefusal(string name)
    {
        lock (_lock)
        {
            return _all.FirstOrDefault(t => t.ProtocolTool.Name == name) is { } tool && Writes(tool)
                ? new DatumException(ErrorCode.NotAllowed, $"{name} can change a model and {ReadOnlyReason}", "Restart Datum without --read-only or DATUM_READ_ONLY to make changes")
                : null;
        }
    }

    private bool Writes(McpServerTool tool) =>
        readOnly && tool.ProtocolTool.Annotations?.ReadOnlyHint != true && !SessionOnly.Contains(tool.ProtocolTool.Name);

    private static string ToolsetOf(McpServerTool tool) => tool.Metadata.OfType<ToolsetAttribute>().FirstOrDefault()?.Name ?? Core;

    private CatalogEntry Describe(McpServerTool tool)
    {
        var toolset = ToolsetOf(tool);
        var reason = UnavailableReason(tool);
        var status = reason is not null ? $"unavailable: {reason}"
            : IsListed(tool) ? Listed
            : $"enable toolset '{toolset}'";
        return new CatalogEntry(tool.ProtocolTool.Name, toolset, tool.ProtocolTool.Description ?? "", status, reason is not null);
    }

    private string? UnavailableReason(McpServerTool tool)
    {
        if (Writes(tool))
        {
            return ReadOnlyReason;
        }

        var requirements = tool.Metadata.OfType<IToolRequirement>().ToList();
        if (requirements.Count == 0)
        {
            return null;
        }

        return _capabilities is null
            ? "SOLIDWORKS isn't connected yet; call sw_status first"
            : requirements.Select(r => r.Check(_capabilities)).FirstOrDefault(r => r is not null);
    }

    private bool IsListed(McpServerTool tool) =>
        (allTools && !Writes(tool)) || ((ToolsetOf(tool) == Core || _enabled.Contains(ToolsetOf(tool))) && UnavailableReason(tool) is null);

    private void Refresh()
    {
        if (_listed is null)
        {
            return;
        }

        using var batch = _listed.DeferChangedEvents();
        foreach (var tool in _all)
        {
            if (IsListed(tool))
            {
                _listed.TryAdd(tool);
            }
            else
            {
                _listed.Remove(tool);
            }
        }
    }
}
