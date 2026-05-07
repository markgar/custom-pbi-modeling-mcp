using Azure.Core;
using Azure.Identity;

namespace PbiModelingMcp.Auth;

/// <summary>
/// Default <see cref="ITokenProvider"/>. Uses
/// <see cref="DefaultAzureCredential"/> so the same code resolves to the
/// right identity in every environment without a config switch:
/// <list type="bullet">
///   <item>In production on Azure (Container Apps, ACI, App Service, AKS),
///         the platform's Managed Identity is used — no secret material.</item>
///   <item>Locally with <c>az login</c>, the developer's user identity
///         is used.</item>
/// </list>
/// The credential's built-in cache handles token refresh; we do not layer
/// our own.
/// </summary>
/// <remarks>
/// PRODUCTION SWAP — backend Power BI auth.
/// This implementation runs every Power BI call as the *server* identity,
/// regardless of which HTTP caller triggered it — the simplest shape that
/// exercises the seam. For real multi-user, ship a second
/// <see cref="ITokenProvider"/> implementation that pulls the inbound
/// user's JWT off <c>HttpContext</c> via <c>IHttpContextAccessor</c> and
/// exchanges it for a Power BI token via MSAL on-behalf-of (OBO).
/// Register that one in DI for the HTTP host (keep this one for stdio);
/// nothing else changes — the <see cref="ITokenProvider"/> contract is
/// the same. See <c>SECURITY.md</c> "Production hardening".
/// </remarks>
internal sealed class DefaultAzureCredentialTokenProvider : ITokenProvider
{
    private static readonly string[] PowerBiScopes =
        ["https://analysis.windows.net/powerbi/api/.default"];

    private readonly TokenCredential _credential = new DefaultAzureCredential();

    public async ValueTask<string> GetPowerBiTokenAsync(CancellationToken ct)
    {
        var token = await _credential
            .GetTokenAsync(new TokenRequestContext(PowerBiScopes), ct)
            .ConfigureAwait(false);
        return token.Token;
    }
}
