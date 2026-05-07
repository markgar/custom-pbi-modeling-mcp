namespace PbiModelingMcp.Configuration;

/// <summary>
/// Server-level settings (audit, safety defaults). Bound from <c>PBI_MCP__*</c>.
/// </summary>
public sealed class ServerOptions
{
    public const string SectionName = "PBI_MCP";

    /// <summary>
    /// Directory where audit logs and TMSL backups are written.
    /// Defaults to <c>~/.pbi-modeling-mcp</c>.
    /// </summary>
    public string? AuditDir { get; set; }

    /// <summary>
    /// When true, <c>delete_*</c> tools must be confirmed before applying.
    /// </summary>
    public bool RequireConfirmDelete { get; set; } = true;

    /// <summary>
    /// Identifier recorded in audit events. Falls back to OS user@host.
    /// </summary>
    public string? Actor { get; set; }

    /// <summary>
    /// Minimum log level. One of <c>Verbose</c>/<c>Debug</c>/<c>Information</c>/
    /// <c>Warning</c>/<c>Error</c>/<c>Fatal</c>. Defaults to <c>Information</c>.
    /// </summary>
    public string? LogLevel { get; set; }

    /// <summary>
    /// Transport the server listens on. <c>"stdio"</c> (default) keeps the
    /// process tied to one MCP client over stdin/stdout. <c>"http"</c> runs
    /// an ASP.NET Core host on <see cref="HttpHost"/>:<see cref="HttpPort"/>
    /// — see the HTTP* fields below.
    /// </summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>
    /// HTTP host/IP to bind when <see cref="Transport"/> is <c>"http"</c>.
    /// Defaults to <c>127.0.0.1</c> — loopback only. Setting this to a
    /// non-loopback address requires either TLS or
    /// <see cref="HttpAllowInsecure"/>=true (so the API key is not
    /// shipped in cleartext).
    /// </summary>
    public string HttpHost { get; set; } = "127.0.0.1";

    /// <summary>
    /// HTTP port to bind when <see cref="Transport"/> is <c>"http"</c>.
    /// Defaults to <c>5000</c>. Override when running behind a reverse
    /// proxy or to avoid a collision.
    /// </summary>
    public int HttpPort { get; set; } = 5000;

    /// <summary>
    /// Shared-secret API key required on every authenticated HTTP
    /// request when <see cref="Transport"/> is <c>"http"</c>. Sent by
    /// the client in the <c>X-Api-Key</c> header (NOT
    /// <c>Authorization: Bearer</c> — see
    /// <see cref="PbiModelingMcp.Http.ApiKeyAuthMiddleware"/> for why).
    /// Must be at least 32 chars and must not match a known sentinel
    /// placeholder; the process refuses to start otherwise. The server
    /// cannot tell two callers with the same key apart — single-tenant
    /// only. See <c>SECURITY.md</c> for the full trust-model writeup.
    /// </summary>
    public string? HttpAuthToken { get; set; }

    /// <summary>
    /// Opt-in flag required to bind <see cref="HttpHost"/> to a non-loopback
    /// address without TLS. Defaults to <c>false</c>; the process refuses
    /// to start in that configuration unless this is explicitly true.
    /// </summary>
    public bool HttpAllowInsecure { get; set; }

    /// <summary>
    /// Opt-in flag that disables the HTTP API-key auth middleware entirely.
    /// Defaults to <c>false</c>. When <c>true</c>, <see cref="HttpAuthToken"/>
    /// is not required and no <c>X-Api-Key</c> header is checked — anyone
    /// who can reach <see cref="HttpHost"/>:<see cref="HttpPort"/> can call
    /// every tool as the server's ambient identity. Intended for local
    /// development on a loopback bind only; combining with non-loopback
    /// host requires <see cref="HttpAllowInsecure"/>=true and is still a
    /// terrible idea. A loud stderr banner is emitted when this is on.
    /// </summary>
    public bool HttpDisableAuth { get; set; }

    /// <summary>
    /// Opt-in flag for **platform-managed** hosting (Azure App Service,
    /// Azure Container Apps, AKS behind an ingress controller, any
    /// reverse proxy that picks the listen port for you). When true:
    /// <list type="bullet">
    ///   <item>Bind on <c>http://*:&lt;port&gt;</c> (all interfaces) instead of
    ///         <see cref="HttpHost"/>.</item>
    ///   <item>Resolve the listen port from the platform's <c>PORT</c> env
    ///         var (App Service / ACA convention) — falling back to
    ///         <c>WEBSITES_PORT</c>, then <see cref="HttpPort"/>.</item>
    ///   <item>Skip the loopback / IP-parse / cleartext-secret guardrails
    ///         in <c>HttpTransportValidation</c> — TLS termination is the
    ///         platform's job, not ours.</item>
    /// </list>
    /// Defaults to <c>false</c>; existing self-hosted shapes (loopback +
    /// reverse proxy on the same box, or direct loopback for local dev)
    /// keep the original guardrails.
    /// </summary>
    public bool HttpListenAllInterfaces { get; set; }

    /// <summary>
    /// Resolve the actual TCP port to listen on, honoring platform
    /// conventions when <see cref="HttpListenAllInterfaces"/> is true.
    /// Order: <c>PORT</c> → <c>WEBSITES_PORT</c> → <see cref="HttpPort"/>.
    /// </summary>
    public int ResolveListenPort()
    {
        if (HttpListenAllInterfaces)
        {
            foreach (var name in new[] { "PORT", "WEBSITES_PORT" })
            {
                var raw = Environment.GetEnvironmentVariable(name);
                if (!string.IsNullOrWhiteSpace(raw)
                    && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                        System.Globalization.CultureInfo.InvariantCulture, out var p)
                    && p > 0)
                {
                    return p;
                }
            }
        }
        return HttpPort;
    }

    public string ResolveAuditDir()
    {
        if (string.IsNullOrWhiteSpace(AuditDir))
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".pbi-modeling-mcp");
        }

        var expanded = AuditDir;
        if (expanded.StartsWith('~'))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var remainder = expanded.Length > 1 ? expanded[1..] : string.Empty;
            expanded = Path.Combine(home, remainder.TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }

        return Environment.ExpandEnvironmentVariables(expanded);
    }

    public string ResolveActor()
        => !string.IsNullOrWhiteSpace(Actor)
            ? Actor
            : $"{Environment.UserName}@{Environment.MachineName}";
}
