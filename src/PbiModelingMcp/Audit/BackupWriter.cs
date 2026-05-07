using System.Threading;
using System.Threading.Tasks;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PbiModelingMcp.Configuration;
using PbiModelingMcp.Connection;

namespace PbiModelingMcp.Audit;

/// <summary>
/// Snapshots a model's TMSL to disk before a mutating operation, so any
/// SaveChanges can be undone manually if needed. Cheap enough to do on every
/// write.
/// </summary>
public interface IBackupWriter
{
    /// <returns>Absolute path of the snapshot file written.</returns>
    Task<string> SnapshotAsync(
        ConnectionDescriptor descriptor,
        Database db,
        string action,
        CancellationToken ct);
}

internal sealed class BackupWriter : IBackupWriter
{
    private readonly string _root;
    private readonly ILogger<BackupWriter> _logger;

    public BackupWriter(IOptions<ServerOptions> options, ILogger<BackupWriter> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _root = Path.Combine(options.Value.ResolveAuditDir(), "backups");
        Directory.CreateDirectory(_root);
    }

    public async Task<string> SnapshotAsync(
        ConnectionDescriptor descriptor,
        Database db,
        string action,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(db);

        var datasetSlug = Slug(descriptor.Dataset);
        var dir = Path.Combine(_root, Slug(descriptor.Workspace), datasetSlug);
        Directory.CreateDirectory(dir);

        var stamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", System.Globalization.CultureInfo.InvariantCulture);
        var file = Path.Combine(dir, $"{stamp}-{Slug(action)}.bim");

        // TOM's serializer is sync; offload so we honor cancellation.
        var tmsl = await Task.Run(
            () => JsonSerializer.SerializeDatabase(db),
            ct).ConfigureAwait(false);

        await File.WriteAllTextAsync(file, tmsl, ct).ConfigureAwait(false);
        _logger.LogInformation("TMSL snapshot written: {File} ({Bytes} bytes)", file, tmsl.Length);
        return file;
    }

    private static string Slug(string s)
    {
        var chars = s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray();
        var slug = new string(chars).Trim('-');
        // Fallback so pathological inputs like "..", "/" or "***" don't slug
        // to empty and collapse a Path.Combine segment.
        return slug.Length == 0 ? "_" : slug;
    }
}
