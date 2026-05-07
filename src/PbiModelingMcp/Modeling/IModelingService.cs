using System.Threading;
using System.Threading.Tasks;
using PbiModelingMcp.Connection;

namespace PbiModelingMcp.Modeling;

/// <summary>
/// All TOM-backed model operations. Tools call these; nothing in the Tools
/// layer touches TOM directly. Every operation requires an explicit
/// <see cref="ConnectionDescriptor"/> — there is no implicit current model.
/// </summary>
public interface IModelingService
{
    Task<IReadOnlyList<TableSummary>> ListTablesAsync(
        ConnectionDescriptor descriptor,
        CancellationToken ct);

    Task<IReadOnlyList<MeasureSummary>> ListMeasuresAsync(
        string table,
        ConnectionDescriptor descriptor,
        CancellationToken ct);

    Task<MeasureDetail> GetMeasureAsync(
        string table,
        string name,
        ConnectionDescriptor descriptor,
        CancellationToken ct);

    /// <summary>Serialize the whole database to TMSL (JSON).</summary>
    Task<string> GetModelTmslAsync(
        ConnectionDescriptor descriptor,
        CancellationToken ct);

    /// <summary>Serialize a single table to TMSL (JSON).</summary>
    Task<string> GetTableTmslAsync(
        string table,
        ConnectionDescriptor descriptor,
        CancellationToken ct);

    Task<WriteResult> AddMeasureAsync(
        AddMeasureRequest request,
        ConnectionDescriptor descriptor,
        CancellationToken ct);

    Task<WriteResult> UpdateMeasureAsync(
        UpdateMeasureRequest request,
        ConnectionDescriptor descriptor,
        CancellationToken ct);

    Task<WriteResult> DeleteMeasureAsync(
        DeleteMeasureRequest request,
        ConnectionDescriptor descriptor,
        CancellationToken ct);
}

public sealed record TableSummary(
    string Name,
    bool IsHidden,
    int MeasureCount,
    int ColumnCount);

public sealed record MeasureSummary(
    string Name,
    string Expression,
    string? FormatString,
    string? DisplayFolder,
    string? Description,
    bool IsHidden);

public sealed record MeasureDetail(
    string Table,
    string Name,
    string Expression,
    string? FormatString,
    string? DisplayFolder,
    string? Description,
    bool IsHidden,
    string? DataType,
    DateTime? ModifiedTimeUtc);

public sealed record AddMeasureRequest(
    string Table,
    string Name,
    string Dax,
    string? FormatString = null,
    string? DisplayFolder = null,
    string? Description = null,
    bool DryRun = false);

/// <summary>
/// Partial-update request. <see cref="Dax"/> / <see cref="FormatString"/> /
/// <see cref="DisplayFolder"/> / <see cref="Description"/> follow the
/// convention: <c>null</c> = leave unchanged, empty string = clear.
/// (JSON-RPC reflection binding cannot distinguish omitted from explicit
/// null, so empty string is the wire-level signal to clear.)
/// </summary>
public sealed record UpdateMeasureRequest(
    string Table,
    string Name,
    string? Dax = null,
    string? FormatString = null,
    string? DisplayFolder = null,
    string? Description = null,
    bool DryRun = false);

public sealed record DeleteMeasureRequest(
    string Table,
    string Name,
    bool DryRun = false);

/// <summary>
/// Result of a mutating operation. <see cref="Outcome"/> is
/// <c>"applied"</c> or <c>"preview"</c> (errors throw). For previews,
/// <see cref="DiffBefore"/> / <see cref="DiffAfter"/> contain TMSL of the
/// affected scope (the table, currently). For applied writes, those are
/// null. <see cref="BackupPath"/> is set whenever a backup was taken.
/// </summary>
public sealed record WriteResult(
    string Action,
    string Outcome,
    string Table,
    string? Measure,
    long DurationMs,
    string? BackupPath,
    string? DiffBefore,
    string? DiffAfter);
