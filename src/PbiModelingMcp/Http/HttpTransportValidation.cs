using System.Net;
using PbiModelingMcp.Configuration;

namespace PbiModelingMcp.Http;

/// <summary>
/// Fail-closed startup checks for the HTTP transport. Any check that fails
/// throws <see cref="InvalidOperationException"/> with a message the
/// operator can act on. Callers translate that into a non-zero exit and
/// stderr diagnostic.
/// </summary>
internal static class HttpTransportValidation
{
    /// <summary>
    /// Sentinel placeholders that operators sometimes leave in env files.
    /// Refuse to start if the configured token matches any of these so the
    /// server never accepts a guessable secret.
    /// </summary>
    internal static readonly IReadOnlySet<string> SentinelTokens =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "REPLACE_ME_WITH_A_SECURE_RANDOM_STRING",
            "INSECURE_DEV_TOKEN",
            "CHANGEME",
            "changeme",
        };

    internal const int MinTokenLength = 32;

    /// <summary>
    /// Validate <paramref name="opts"/> for an HTTP transport launch.
    /// Throws if any guardrail is tripped. Returns the resolved actor and
    /// the parsed bind <see cref="IPAddress"/> as a side benefit so the
    /// caller doesn't reparse them.
    /// </summary>
    public static (IPAddress BindAddress, bool IsLoopback) ValidateForHttp(ServerOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        if (!opts.HttpDisableAuth)
        {
            var token = opts.HttpAuthToken;
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new InvalidOperationException(
                    "PBI_MCP__HttpAuthToken is required when PBI_MCP__Transport=http. " +
                    "Generate one with: " +
                    "[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))");
            }

            if (SentinelTokens.Contains(token))
            {
                throw new InvalidOperationException(
                    "PBI_MCP__HttpAuthToken matches a known placeholder value. " +
                    "Replace it with a real random secret (>= 32 chars).");
            }

            if (token.Length < MinTokenLength)
            {
                throw new InvalidOperationException(
                    $"PBI_MCP__HttpAuthToken must be at least {MinTokenLength} characters " +
                    $"(got {token.Length}). Generate one with: " +
                    "[Convert]::ToBase64String([System.Security.Cryptography.RandomNumberGenerator]::GetBytes(32))");
            }
        }

        if (string.IsNullOrWhiteSpace(opts.HttpHost))
        {
            throw new InvalidOperationException("PBI_MCP__HttpHost cannot be empty.");
        }

        // Platform-managed hosting: TLS termination + bind address are the
        // platform's job. Skip the loopback / IP-parse / cleartext-secret
        // guardrails — they exist to protect against accidentally shipping
        // the API key over the network on a self-hosted bind, which
        // doesn't apply when something upstream is doing TLS for us.
        if (opts.HttpListenAllInterfaces)
        {
            return (IPAddress.Any, IsLoopback: false);
        }

        if (!IPAddress.TryParse(opts.HttpHost, out var bindAddress))
        {
            throw new InvalidOperationException(
                $"PBI_MCP__HttpHost must be an IP address (got '{opts.HttpHost}'). " +
                "Use 127.0.0.1 for loopback or 0.0.0.0 for all interfaces.");
        }

        var loopback = IPAddress.IsLoopback(bindAddress);
        if (!loopback && !opts.HttpAllowInsecure)
        {
            throw new InvalidOperationException(
                $"PBI_MCP__HttpHost={opts.HttpHost} binds non-loopback without TLS. " +
                "Either run behind a TLS-terminating reverse proxy and set " +
                "PBI_MCP__HttpAllowInsecure=true, or bind to 127.0.0.1.");
        }

        return (bindAddress, loopback);
    }

    /// <summary>
    /// One-line stderr banner emitted on every HTTP launch so the operator
    /// always sees the trust-model summary, even if logs are silenced.
    /// </summary>
    public static string BuildStartupBanner(ServerOptions opts, string actor, bool isLoopback)
    {
        var port = opts.ResolveListenPort();
        if (opts.HttpListenAllInterfaces)
        {
            // Platform-managed: bind on all interfaces, TLS terminated upstream.
            // Auth-disabled is even more dangerous here than on loopback because
            // the platform exposes us publicly.
            if (opts.HttpDisableAuth)
            {
                return
                    $"HTTP transport: AUTH DISABLED on *:{port} (platform-managed, TLS upstream). " +
                    $"ANY caller routed in by the platform calls Power BI as '{actor}'. " +
                    "Do NOT run this configuration anywhere reachable.";
            }
            return
                $"HTTP transport: shared-secret API-key auth enabled on *:{port} (platform-managed, TLS upstream). " +
                $"Server identity '{actor}' will act on Power BI for ALL authenticated callers. " +
                "Not multi-user safe.";
        }

        var exposure = isLoopback ? "loopback" : "non-loopback";
        var tls = opts.HttpAllowInsecure && !isLoopback ? " (insecure: API key over cleartext network)" : string.Empty;
        if (opts.HttpDisableAuth)
        {
            return
                $"HTTP transport: AUTH DISABLED on {opts.HttpHost}:{port} ({exposure}). " +
                $"Anyone who can reach this address calls Power BI as '{actor}'. " +
                "Loopback dev only — do not use in any shared environment.";
        }
        return
            $"HTTP transport: shared-secret API-key auth enabled on {opts.HttpHost}:{port} ({exposure}){tls}. " +
            $"Server identity '{actor}' will act on Power BI for ALL authenticated callers. " +
            "Not multi-user safe.";
    }
}
