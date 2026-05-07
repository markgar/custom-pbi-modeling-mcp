using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Extensions.Logging;

namespace PbiModelingMcp.Connection;

/// <summary>
/// Per-call TOM <see cref="Server"/> lifecycle, with a per-descriptor
/// semaphore so concurrent calls against the same model serialize (TOM is
/// not thread-safe). Different models run in parallel.
/// </summary>
/// <remarks>
/// The server is stateless: there is no "current" connection. Every tool
/// passes <c>workspace</c> + <c>dataset</c> explicitly so stdio and HTTP
/// transports behave identically and concurrent HTTP callers cannot
/// retarget each other.
/// </remarks>
public interface IConnectionManager
{
    /// <summary>
    /// Acquire the descriptor's semaphore, open a fresh <see cref="Server"/>,
    /// run <paramref name="body"/>, dispose, and return its result.
    /// </summary>
    Task<T> WithServerAsync<T>(
        ConnectionDescriptor descriptor,
        Func<Server, Database, CancellationToken, Task<T>> body,
        CancellationToken ct);
}

internal sealed class ConnectionManager : IConnectionManager
{
    private readonly ITomServerFactory _factory;
    private readonly ILogger<ConnectionManager> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.OrdinalIgnoreCase);

    public ConnectionManager(
        ITomServerFactory factory,
        ILogger<ConnectionManager> logger)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(logger);
        _factory = factory;
        _logger = logger;
    }

    public async Task<T> WithServerAsync<T>(
        ConnectionDescriptor descriptor,
        Func<Server, Database, CancellationToken, Task<T>> body,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(body);

        var sem = _locks.GetOrAdd(descriptor.ToString(), _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var server = await _factory.OpenAsync(descriptor, ct).ConfigureAwait(false);

            // TOM accepts either dataset name or ID via Initial Catalog, but the
            // Databases collection is keyed by ID. Try name first, then ID match.
            var db = server.Databases.FindByName(descriptor.Dataset)
                ?? server.Databases
                    .Cast<Database>()
                    .FirstOrDefault(d => string.Equals(d.ID, descriptor.Dataset, StringComparison.OrdinalIgnoreCase));
            if (db is null)
            {
                throw new InvalidOperationException(
                    $"Dataset '{descriptor.Dataset}' not found in workspace '{descriptor.Workspace}'.");
            }

            _logger.LogDebug("Opened TOM Server for {Descriptor}", descriptor);
            return await body(server, db, ct).ConfigureAwait(false);
        }
        finally
        {
            sem.Release();
        }
    }
}
