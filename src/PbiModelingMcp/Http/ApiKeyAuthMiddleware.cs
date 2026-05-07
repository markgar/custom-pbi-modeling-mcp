using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PbiModelingMcp.Http;

/// <summary>
/// Constant-time API-key check. Any request reaching this middleware
/// must present a header named <see cref="HeaderName"/> whose value
/// matches <see cref="_expected"/>. Anything else returns 401 with no
/// body — we do not distinguish "missing" from "wrong" externally.
/// </summary>
/// <remarks>
/// <para>
/// We deliberately use a custom header (<c>X-Api-Key</c>) rather than
/// <c>Authorization: Bearer …</c>. Spec-compliant MCP clients (notably
/// the VS Code MCP client) treat any 401 from a server they sent a
/// <c>Bearer</c> credential to as the trigger for OAuth 2.1
/// protected-resource discovery and dynamic client registration —
/// regardless of whether the server emits a <c>WWW-Authenticate</c>
/// challenge. Using a non-Authorization header sidesteps the OAuth
/// state machine entirely so that a static shared-secret key works
/// without the client trying to "upgrade" to OAuth.
/// </para>
/// <para>
/// The <c>/healthz</c> liveness route is exempted by an upstream branch
/// in the request pipeline (see <c>HttpServerHost</c>); this middleware
/// is only installed for routes behind the auth gate.
/// </para>
/// </remarks>
internal sealed class ApiKeyAuthMiddleware
{
    /// <summary>
    /// HTTP header the server reads the shared secret from.
    /// </summary>
    public const string HeaderName = "X-Api-Key";

    private readonly RequestDelegate _next;
    private readonly byte[] _expected;
    private readonly ILogger<ApiKeyAuthMiddleware> _logger;

    public ApiKeyAuthMiddleware(
        RequestDelegate next,
        string expectedToken,
        ILogger<ApiKeyAuthMiddleware> logger)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(expectedToken);
        ArgumentNullException.ThrowIfNull(logger);
        _next = next;
        _expected = Encoding.UTF8.GetBytes(expectedToken);
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!TryGetKey(context, out var presented) || !ConstantTimeEquals(presented, _expected))
        {
            _logger.LogWarning(
                "Rejecting unauthenticated MCP request from {Remote} {Method} {Path}",
                context.Connection.RemoteIpAddress,
                context.Request.Method,
                context.Request.Path);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool TryGetKey(HttpContext context, out byte[] token)
    {
        token = Array.Empty<byte>();
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return false;
        }

        foreach (var raw in values)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }

            token = Encoding.UTF8.GetBytes(raw.Trim());
            return token.Length > 0;
        }

        return false;
    }

    private static bool ConstantTimeEquals(byte[] a, byte[] b)
        => CryptographicOperations.FixedTimeEquals(a, b);
}
