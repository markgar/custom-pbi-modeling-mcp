using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PbiModelingMcp.Audit;
using PbiModelingMcp.Configuration;
using PbiModelingMcp.Connection;
using PbiModelingMcp.Modeling;

namespace PbiModelingMcp.Tests.Modeling;

/// <summary>
/// Builds a <see cref="ModelingService"/> wired to no-op infrastructure for
/// argument-validation tests. Any code path that reaches the
/// connection/audit/backup layer will throw — these stubs are deliberately
/// inert, so tests must exit at the validation layer.
/// </summary>
internal static class TestModelingService
{
    public static IModelingService Create()
    {
        var conn = new ThrowingConnection();
        var audit = new NullAudit();
        var backup = new NullBackup();
        var opts = Options.Create(new ServerOptions());
        return new ModelingService(conn, audit, backup, opts, NullLogger<ModelingService>.Instance);
    }

    private sealed class ThrowingConnection : IConnectionManager
    {
        public Task<T> WithServerAsync<T>(
            ConnectionDescriptor descriptor,
            Func<Server, Database, CancellationToken, Task<T>> body,
            CancellationToken ct)
            => throw new NotSupportedException(
                "Validation tests must not reach the connection layer.");
    }

    private sealed class NullAudit : IAuditLogger
    {
        public Task LogAsync(AuditEvent evt, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullBackup : IBackupWriter
    {
        public Task<string> SnapshotAsync(
            ConnectionDescriptor descriptor, Database db, string action, CancellationToken ct)
            => Task.FromResult("/dev/null");
    }
}
