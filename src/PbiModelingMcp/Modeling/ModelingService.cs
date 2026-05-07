using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PbiModelingMcp.Audit;
using PbiModelingMcp.Configuration;
using PbiModelingMcp.Connection;
using TomJsonSerializer = Microsoft.AnalysisServices.Tabular.JsonSerializer;

namespace PbiModelingMcp.Modeling;

internal sealed class ModelingService : IModelingService
{
    private readonly IConnectionManager _conn;
    private readonly IAuditLogger _audit;
    private readonly IBackupWriter _backup;
    private readonly ServerOptions _serverOptions;
    private readonly ILogger<ModelingService> _logger;

    public ModelingService(
        IConnectionManager conn,
        IAuditLogger audit,
        IBackupWriter backup,
        IOptions<ServerOptions> serverOptions,
        ILogger<ModelingService> logger)
    {
        ArgumentNullException.ThrowIfNull(conn);
        ArgumentNullException.ThrowIfNull(audit);
        ArgumentNullException.ThrowIfNull(backup);
        ArgumentNullException.ThrowIfNull(serverOptions);
        ArgumentNullException.ThrowIfNull(logger);
        _conn = conn;
        _audit = audit;
        _backup = backup;
        _serverOptions = serverOptions.Value;
        _logger = logger;
    }

    public Task<IReadOnlyList<TableSummary>> ListTablesAsync(
        ConnectionDescriptor descriptor,
        CancellationToken ct)
        => _conn.WithServerAsync(descriptor, (_, db, _) =>
        {
            var model = RequireModel(db);
            var tables = model.Tables
                .Select(t => new TableSummary(
                    t.Name,
                    t.IsHidden,
                    t.Measures.Count,
                    t.Columns.Count))
                .ToArray();

            return Task.FromResult<IReadOnlyList<TableSummary>>(tables);
        }, ct);

    public Task<IReadOnlyList<MeasureSummary>> ListMeasuresAsync(
        string table,
        ConnectionDescriptor descriptor,
        CancellationToken ct)
    {
        ValidateNonEmpty(table, nameof(table));
        return _conn.WithServerAsync(descriptor, (_, db, _) =>
        {
            var t = RequireTable(db, table);
            var measures = t.Measures
                .Select(MapSummary)
                .ToArray();
            return Task.FromResult<IReadOnlyList<MeasureSummary>>(measures);
        }, ct);
    }

    public Task<MeasureDetail> GetMeasureAsync(
        string table,
        string name,
        ConnectionDescriptor descriptor,
        CancellationToken ct)
    {
        ValidateNonEmpty(table, nameof(table));
        ValidateNonEmpty(name, nameof(name));
        return _conn.WithServerAsync(descriptor, (_, db, _) =>
        {
            var t = RequireTable(db, table);
            var m = t.Measures.Find(name)
                ?? throw new InvalidOperationException(
                    $"Measure '{name}' not found on table '{table}'.");
            return Task.FromResult(MapDetail(table, m));
        }, ct);
    }

    public Task<string> GetModelTmslAsync(
        ConnectionDescriptor descriptor,
        CancellationToken ct)
        => _conn.WithServerAsync(descriptor, (_, db, _) =>
            Task.FromResult(TomJsonSerializer.SerializeDatabase(db)), ct);

    public Task<string> GetTableTmslAsync(
        string table,
        ConnectionDescriptor descriptor,
        CancellationToken ct)
    {
        ValidateNonEmpty(table, nameof(table));
        return _conn.WithServerAsync(descriptor, (_, db, _) =>
        {
            var t = RequireTable(db, table);
            return Task.FromResult(TomJsonSerializer.SerializeObject(t));
        }, ct);
    }

    public Task<WriteResult> AddMeasureAsync(
        AddMeasureRequest request,
        ConnectionDescriptor descriptor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateNonEmpty(request.Table, nameof(request.Table));
        ValidateNonEmpty(request.Name, nameof(request.Name));
        ValidateNonEmpty(request.Dax, nameof(request.Dax));

        var args = new Dictionary<string, object?>
        {
            ["table"] = request.Table,
            ["name"] = request.Name,
            ["dax"] = request.Dax,
            ["formatString"] = request.FormatString,
            ["displayFolder"] = request.DisplayFolder,
            ["description"] = request.Description,
        };

        return RunWriteAsync(
            action: "add_measure",
            args: args,
            request.Table,
            request.Name,
            request.DryRun,
            descriptor,
            apply: table =>
            {
                if (table.Measures.Find(request.Name) is not null)
                {
                    throw new InvalidOperationException(
                        $"Measure '{request.Name}' already exists on table '{request.Table}'.");
                }

                var m = new Measure
                {
                    Name = request.Name,
                    Expression = request.Dax,
                };
                ApplyOptional(request.FormatString, v => m.FormatString = v ?? string.Empty);
                ApplyOptional(request.DisplayFolder, v => m.DisplayFolder = v ?? string.Empty);
                ApplyOptional(request.Description, v => m.Description = v ?? string.Empty);
                table.Measures.Add(m);
            },
            ct);
    }

    public Task<WriteResult> UpdateMeasureAsync(
        UpdateMeasureRequest request,
        ConnectionDescriptor descriptor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateNonEmpty(request.Table, nameof(request.Table));
        ValidateNonEmpty(request.Name, nameof(request.Name));

        if (request.Dax is null
            && request.FormatString is null
            && request.DisplayFolder is null
            && request.Description is null)
        {
            throw new ArgumentException(
                "update_measure requires at least one field to change.", nameof(request));
        }

        var args = new Dictionary<string, object?>
        {
            ["table"] = request.Table,
            ["name"] = request.Name,
            ["dax"] = request.Dax,
            ["formatString"] = request.FormatString,
            ["displayFolder"] = request.DisplayFolder,
            ["description"] = request.Description,
        };

        return RunWriteAsync(
            action: "update_measure",
            args: args,
            request.Table,
            request.Name,
            request.DryRun,
            descriptor,
            apply: table =>
            {
                var m = table.Measures.Find(request.Name)
                    ?? throw new InvalidOperationException(
                        $"Measure '{request.Name}' not found on table '{request.Table}'.");

                ApplyOptional(request.Dax, v =>
                {
                    if (string.IsNullOrEmpty(v))
                    {
                        throw new ArgumentException("dax cannot be cleared on a measure.");
                    }
                    m.Expression = v;
                });
                ApplyOptional(request.FormatString, v => m.FormatString = v ?? string.Empty);
                ApplyOptional(request.DisplayFolder, v => m.DisplayFolder = v ?? string.Empty);
                ApplyOptional(request.Description, v => m.Description = v ?? string.Empty);
            },
            ct);
    }

    public Task<WriteResult> DeleteMeasureAsync(
        DeleteMeasureRequest request,
        ConnectionDescriptor descriptor,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateNonEmpty(request.Table, nameof(request.Table));
        ValidateNonEmpty(request.Name, nameof(request.Name));

        var args = new Dictionary<string, object?>
        {
            ["table"] = request.Table,
            ["name"] = request.Name,
        };

        return RunWriteAsync(
            action: "delete_measure",
            args: args,
            request.Table,
            request.Name,
            request.DryRun,
            descriptor,
            apply: table =>
            {
                var m = table.Measures.Find(request.Name)
                    ?? throw new InvalidOperationException(
                        $"Measure '{request.Name}' not found on table '{request.Table}'.");
                table.Measures.Remove(m);
            },
            ct);
    }

    /// <summary>
    /// Safety pipeline shared by all measure mutations: pre-validate target
    /// table, audit pre, take backup (real writes only), apply via TOM,
    /// audit post. Errors emit a post event with structured error info and
    /// re-throw.
    /// </summary>
    private Task<WriteResult> RunWriteAsync(
        string action,
        IReadOnlyDictionary<string, object?> args,
        string tableName,
        string measureName,
        bool dryRun,
        ConnectionDescriptor descriptor,
        Action<Table> apply,
        CancellationToken ct)
    {
        return _conn.WithServerAsync(descriptor, async (_, db, innerCt) =>
        {
            var resolved = AuditDescriptor.From(descriptor);
            var actor = _serverOptions.ResolveActor();
            var requestId = Guid.NewGuid().ToString("N");
            var sw = Stopwatch.StartNew();
            string? backupPath = null;
            string? diffBefore = null;
            string? diffAfter = null;

            // pre-validate: table must exist before we audit/backup so the
            // failure is cheap and visible.
            var table = RequireTable(db, tableName);

            await _audit.LogAsync(new AuditEvent(
                Schema: AuditSchema.Version,
                TimestampUtc: DateTime.UtcNow,
                Phase: "pre",
                Action: action,
                Descriptor: resolved,
                Args: SanitizeArgs(args),
                DryRun: dryRun,
                Actor: actor,
                RequestId: requestId), innerCt).ConfigureAwait(false);

            try
            {
                if (dryRun)
                {
                    diffBefore = TomJsonSerializer.SerializeObject(table);
                    apply(table);
                    diffAfter = TomJsonSerializer.SerializeObject(table);
                    // Discard in-memory edits so nothing is persisted. On a real
                    // connection this always succeeds; we wrap defensively so a
                    // disconnected/test scenario can't fail here (the Server is
                    // disposed at the end of WithServerAsync regardless).
                    try { db.Model.UndoLocalChanges(); }
                    catch (Exception undoEx)
                    {
                        _logger.LogWarning(undoEx, "UndoLocalChanges failed after {Action} dry-run", action);
                    }
                }
                else
                {
                    backupPath = await _backup
                        .SnapshotAsync(descriptor, db, action, innerCt)
                        .ConfigureAwait(false);
                    apply(table);
                    db.Model.SaveChanges();
                }

                sw.Stop();
                var outcome = dryRun ? "preview" : "applied";
                await _audit.LogAsync(new AuditEvent(
                    Schema: AuditSchema.Version,
                    TimestampUtc: DateTime.UtcNow,
                    Phase: "post",
                    Action: action,
                    Descriptor: resolved,
                    Args: SanitizeArgs(args),
                    DryRun: dryRun,
                    Actor: actor,
                    RequestId: requestId,
                    Outcome: outcome,
                    DurationMs: sw.ElapsedMilliseconds,
                    BackupPath: backupPath), innerCt).ConfigureAwait(false);

                _logger.LogInformation(
                    "{Action} {Outcome} table={Table} name={Name} duration={Ms}ms",
                    action, outcome, tableName, measureName, sw.ElapsedMilliseconds);

                return new WriteResult(
                    Action: action,
                    Outcome: outcome,
                    Table: tableName,
                    Measure: measureName,
                    DurationMs: sw.ElapsedMilliseconds,
                    BackupPath: backupPath,
                    DiffBefore: diffBefore,
                    DiffAfter: diffAfter);
            }
            catch (Exception ex)
            {
                sw.Stop();
                // Roll back any in-memory edits before disposing the server,
                // so a retry inside the same call sees a clean state.
                try { db.Model?.UndoLocalChanges(); }
                catch (Exception undoEx)
                {
                    _logger.LogWarning(undoEx, "UndoLocalChanges failed after {Action} error", action);
                }

                await _audit.LogAsync(new AuditEvent(
                    Schema: AuditSchema.Version,
                    TimestampUtc: DateTime.UtcNow,
                    Phase: "post",
                    Action: action,
                    Descriptor: resolved,
                    Args: SanitizeArgs(args),
                    DryRun: dryRun,
                    Actor: actor,
                    RequestId: requestId,
                    Outcome: "error",
                    DurationMs: sw.ElapsedMilliseconds,
                    Error: new AuditError(ex.GetType().Name, ex.Message),
                    BackupPath: backupPath), CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }, ct);
    }

    private static Model RequireModel(Database db)
        => db.Model ?? throw new InvalidOperationException("Database has no model.");

    private static Table RequireTable(Database db, string name)
    {
        var model = RequireModel(db);
        return model.Tables.Find(name)
            ?? throw new InvalidOperationException($"Table '{name}' not found.");
    }

    private static void ValidateNonEmpty(string value, string paramName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"{paramName} is required.", paramName);
        }
    }

    /// <summary>
    /// Defensive scrub of audit-event <c>args</c>: any key whose name suggests
    /// it carries a credential is redacted before the dict reaches the audit
    /// log on disk. No tool today passes such a key — this is a seam so a
    /// future tool can't accidentally leak one.
    /// </summary>
    /// <remarks>
    /// Exposed <c>internal</c> for unit tests.
    /// </remarks>
    internal static IReadOnlyDictionary<string, object?> SanitizeArgs(
        IReadOnlyDictionary<string, object?> args)
    {
        Dictionary<string, object?>? scrubbed = null;
        foreach (var kvp in args)
        {
            if (IsSecretKey(kvp.Key))
            {
                scrubbed ??= new Dictionary<string, object?>(args, StringComparer.Ordinal);
                scrubbed[kvp.Key] = "[REDACTED]";
            }
        }
        return scrubbed ?? args;
    }

    private static bool IsSecretKey(string key)
        => key.Contains("secret", StringComparison.OrdinalIgnoreCase)
        || key.Contains("password", StringComparison.OrdinalIgnoreCase)
        || key.Contains("token", StringComparison.OrdinalIgnoreCase)
        || key.Contains("credential", StringComparison.OrdinalIgnoreCase)
        || key.Contains("apikey", StringComparison.OrdinalIgnoreCase);

    private static void ApplyOptional(string? value, Action<string?> setter)
    {
        // Convention: null = leave unchanged, empty string = clear.
        if (value is null)
        {
            return;
        }

        setter(value.Length == 0 ? null : value);
    }

    private static MeasureSummary MapSummary(Measure m)
        => new(
            m.Name,
            m.Expression ?? string.Empty,
            string.IsNullOrEmpty(m.FormatString) ? null : m.FormatString,
            string.IsNullOrEmpty(m.DisplayFolder) ? null : m.DisplayFolder,
            string.IsNullOrEmpty(m.Description) ? null : m.Description,
            m.IsHidden);

    private static MeasureDetail MapDetail(string table, Measure m)
    {
        DateTime? modified = m.ModifiedTime == default ? null : m.ModifiedTime.ToUniversalTime();
        return new MeasureDetail(
            table,
            m.Name,
            m.Expression ?? string.Empty,
            string.IsNullOrEmpty(m.FormatString) ? null : m.FormatString,
            string.IsNullOrEmpty(m.DisplayFolder) ? null : m.DisplayFolder,
            string.IsNullOrEmpty(m.Description) ? null : m.Description,
            m.IsHidden,
            m.DataType.ToString(),
            modified);
    }
}
