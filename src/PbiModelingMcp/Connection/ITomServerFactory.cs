using System.Threading;
using System.Threading.Tasks;
using Microsoft.AnalysisServices.Tabular;

namespace PbiModelingMcp.Connection;

/// <summary>
/// Builds and opens a TOM <see cref="Server"/> for a given
/// <see cref="ConnectionDescriptor"/>. Per-call: no pooling.
/// </summary>
public interface ITomServerFactory
{
    /// <summary>
    /// Open a connected <see cref="Server"/>. Caller owns the lifetime
    /// (must <c>Dispose</c>).
    /// </summary>
    Task<Server> OpenAsync(ConnectionDescriptor descriptor, CancellationToken ct);
}
