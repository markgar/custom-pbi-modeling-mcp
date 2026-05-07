using System.Threading;
using System.Threading.Tasks;

namespace PbiModelingMcp.Auth;

/// <summary>
/// Acquires Azure AD bearer tokens for the Power BI XMLA / REST audience.
/// Pluggable so we can swap to certificate or managed-identity auth later
/// without touching <c>ModelingService</c>.
/// </summary>
public interface ITokenProvider
{
    /// <summary>
    /// AAD bearer token for <c>https://analysis.windows.net/powerbi/api/.default</c>.
    /// Implementations are expected to cache + refresh internally.
    /// </summary>
    ValueTask<string> GetPowerBiTokenAsync(CancellationToken ct);
}
