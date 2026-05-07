using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using PbiModelingMcp.Configuration;

namespace PbiModelingMcp.Http;

/// <summary>
/// Builds the HTTP host (Streamable HTTP MCP transport + API-key auth +
/// <c>/healthz</c>). Extracted from <c>Program.cs</c> so integration tests
/// can construct the same pipeline against an in-process
/// <see cref="WebApplication"/>.
/// </summary>
internal static class HttpServerHost
{
    /// <summary>
    /// Apply the HTTP-transport pipeline to <paramref name="builder"/> and
    /// return the built <see cref="WebApplication"/>. Caller is responsible
    /// for starting/disposing it. Does not call <c>app.Urls.Add</c>; the
    /// caller chooses the bind address (production binds via
    /// <see cref="ServerOptions"/>; tests use the in-memory test server).
    /// </summary>
    public static WebApplication Build(WebApplicationBuilder builder, ServerOptions opts)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(opts);

        builder.Services
            .AddMcpServer()
            .WithHttpTransport(o => o.Stateless = true)
            .WithToolsFromAssembly();

        var app = builder.Build();

        // Liveness probe — exempt from auth so orchestrators can hit it
        // without owning the secret. Returns 200 if the process is up.
        app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }));

        // Custom-header API-key gate. Constant-time compare; 401 with no
        // body on any failure (no information leak about whether the
        // header was missing or wrong).
        //
        // We use a custom header (`X-Api-Key`) rather than
        // `Authorization: Bearer ...` deliberately: spec-compliant MCP
        // clients (notably VS Code's MCP client) treat any 401 from a
        // server they sent a Bearer credential to as the trigger for
        // OAuth 2.1 protected-resource discovery + dynamic client
        // registration. With a non-Authorization header the OAuth state
        // machine never activates, so a static shared secret works
        // without the client trying to "upgrade" to OAuth.
        //
        // Skipped entirely when ServerOptions.HttpDisableAuth is true; in
        // that mode the loud stderr banner already announced "AUTH DISABLED"
        // and no key is checked. Loopback dev only.
        //
        // PRODUCTION SWAP — inbound auth.
        // The shared-key middleware here is the simplest shape that
        // exercises the gate: it proves the seam exists and the rest
        // of the pipeline is unaware of which scheme guards it. For a
        // real multi-user deployment, either (a) turn on App Service
        // Easy Auth in front of this process and set
        // HttpDisableAuth=true, or (b) replace this middleware with
        // `Microsoft.AspNetCore.Authentication.JwtBearer` configured
        // against your tenant and require an authorization policy on
        // `MapMcp()`. The line below is the only thing that changes;
        // nothing downstream of `app.MapMcp()` cares.
        // See SECURITY.md "Production hardening".
        if (!opts.HttpDisableAuth)
        {
            app.UseWhen(
                ctx => !IsExemptFromAuth(ctx),
                branch => branch.UseMiddleware<ApiKeyAuthMiddleware>(opts.HttpAuthToken!));
        }

        app.MapMcp();
        return app;
    }

    private static bool IsExemptFromAuth(HttpContext ctx)
        => ctx.Request.Path.Equals("/healthz", StringComparison.OrdinalIgnoreCase);
}
