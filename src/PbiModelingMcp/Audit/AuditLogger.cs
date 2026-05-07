using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PbiModelingMcp.Configuration;
using PbiModelingMcp.Connection;

namespace PbiModelingMcp.Audit;

/// <summary>
/// Append-only JSON-Lines audit log. One file per UTC date, written under
/// <c>{AuditDir}/audit/audit-{yyyy-MM-dd}.log</c>. Schema documented in
/// <c>docs/audit-schema.md</c>.
/// </summary>
public interface IAuditLogger
{
    Task LogAsync(AuditEvent evt, CancellationToken ct);
}

/// <summary>
/// One audit-log line. <see cref="Phase"/> is <c>"pre"</c> or <c>"post"</c>;
/// <see cref="Outcome"/>, <see cref="DurationMs"/>, <see cref="Error"/> and
/// <see cref="BackupPath"/> are only populated on <c>"post"</c>.
/// <see cref="Transport"/> and <see cref="CallerIp"/> are populated when the
/// server is reached over a transport that exposes a remote caller (HTTP);
/// for stdio they are both null.
/// </summary>
public sealed record AuditEvent(
    [property: JsonPropertyName("schema")] int Schema,
    [property: JsonPropertyName("ts")] DateTime TimestampUtc,
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("action")] string Action,
    [property: JsonPropertyName("descriptor")] AuditDescriptor? Descriptor,
    [property: JsonPropertyName("args")] IReadOnlyDictionary<string, object?>? Args,
    [property: JsonPropertyName("dryRun")] bool DryRun,
    [property: JsonPropertyName("actor")] string Actor,
    [property: JsonPropertyName("requestId")] string? RequestId = null,
    [property: JsonPropertyName("outcome")] string? Outcome = null,
    [property: JsonPropertyName("durationMs")] long? DurationMs = null,
    [property: JsonPropertyName("error")] AuditError? Error = null,
    [property: JsonPropertyName("backupPath")] string? BackupPath = null,
    [property: JsonPropertyName("transport")] string? Transport = null,
    [property: JsonPropertyName("callerIp")] string? CallerIp = null);

public sealed record AuditDescriptor(
    [property: JsonPropertyName("workspace")] string Workspace,
    [property: JsonPropertyName("dataset")] string Dataset)
{
    public static AuditDescriptor From(ConnectionDescriptor d)
        => new(d.Workspace, d.Dataset);
}

public sealed record AuditError(
    [property: JsonPropertyName("type")] string Type,
    [property: JsonPropertyName("message")] string Message);

/// <summary>
/// Schema version of every audit event written by this server. See
/// <c>docs/audit-schema.md</c> for changelog.
/// </summary>
public static class AuditSchema
{
    public const int Version = 1;
}

internal sealed class AuditLogger : IAuditLogger, IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly string _dir;
    private readonly ILogger<AuditLogger> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public void Dispose() => _writeLock.Dispose();

    public AuditLogger(IOptions<ServerOptions> options, ILogger<AuditLogger> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _dir = Path.Combine(options.Value.ResolveAuditDir(), "audit");
        Directory.CreateDirectory(_dir);
    }

    public async Task LogAsync(AuditEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var line = JsonSerializer.Serialize(evt, JsonOpts);
        var path = Path.Combine(_dir, $"audit-{evt.TimestampUtc:yyyy-MM-dd}.log");

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(path, line + Environment.NewLine, ct)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to write audit event to {Path}", path);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
