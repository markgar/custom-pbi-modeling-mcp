using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Server;
using PbiModelingMcp.Connection;
using PbiModelingMcp.Modeling;

namespace PbiModelingMcp.Tools;

/// <summary>
/// Read-only model inspection tools. Every tool requires
/// <c>workspace</c> + <c>dataset</c> — the server holds no implicit
/// "current" connection.
/// </summary>
[McpServerToolType]
public sealed class ModelTools
{
    private readonly IModelingService _modeling;

    public ModelTools(IModelingService modeling)
    {
        ArgumentNullException.ThrowIfNull(modeling);
        _modeling = modeling;
    }

    [McpServerTool(Name = "list_tables")]
    [Description("List the tables in a Power BI model. Returns name, hidden flag, and counts of measures and columns per table.")]
    public Task<IReadOnlyList<TableSummary>> ListTablesAsync(
        [Description("Workspace name (preferred) or workspace GUID.")] string workspace,
        [Description("Dataset (semantic model) name or GUID.")] string dataset,
        CancellationToken ct = default)
        => _modeling.ListTablesAsync(BuildDescriptor(workspace, dataset), ct);

    [McpServerTool(Name = "list_measures")]
    [Description("List the measures on a table. Returns name, DAX expression, format string, display folder, description, and hidden flag.")]
    public Task<IReadOnlyList<MeasureSummary>> ListMeasuresAsync(
        [Description("Workspace name (preferred) or workspace GUID.")] string workspace,
        [Description("Dataset (semantic model) name or GUID.")] string dataset,
        [Description("Table name (case-sensitive, as it appears in the model).")] string table,
        CancellationToken ct = default)
        => _modeling.ListMeasuresAsync(table, BuildDescriptor(workspace, dataset), ct);

    [McpServerTool(Name = "get_measure")]
    [Description("Fetch the full detail of a single measure (DAX, format, folder, description, data type, last-modified time).")]
    public Task<MeasureDetail> GetMeasureAsync(
        [Description("Workspace name (preferred) or workspace GUID.")] string workspace,
        [Description("Dataset (semantic model) name or GUID.")] string dataset,
        [Description("Table name owning the measure.")] string table,
        [Description("Measure name.")] string name,
        CancellationToken ct = default)
        => _modeling.GetMeasureAsync(table, name, BuildDescriptor(workspace, dataset), ct);

    internal static ConnectionDescriptor BuildDescriptor(string workspace, string dataset)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            throw new ArgumentException("workspace is required.", nameof(workspace));
        }

        if (string.IsNullOrWhiteSpace(dataset))
        {
            throw new ArgumentException("dataset is required.", nameof(dataset));
        }

        return new ConnectionDescriptor(workspace, dataset);
    }
}
