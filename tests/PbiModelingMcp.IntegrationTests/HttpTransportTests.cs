using System.Net;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PbiModelingMcp.Auth;
using PbiModelingMcp.Configuration;
using PbiModelingMcp.Connection;
using PbiModelingMcp.Http;
using PbiModelingMcp.Modeling;
using PbiModelingMcp.PowerBi;
using Xunit;

namespace PbiModelingMcp.IntegrationTests;

/// <summary>
/// Boots the HTTP host in-process via <see cref="TestServer"/> with all
/// real PBI dependencies stubbed out, then exercises the auth gate and
/// liveness probe documented in <c>SPEC.md</c>.
/// </summary>
public class HttpTransportTests : IAsyncLifetime
{
    private const string ValidToken = "test-token-of-sufficient-length-1234"; // 36 chars
    private WebApplication? _app;
    private HttpClient? _client;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();

        var opts = new ServerOptions
        {
            Transport = "http",
            HttpHost = "127.0.0.1",
            HttpAuthToken = ValidToken,
        };
        builder.Services.AddSingleton<Microsoft.Extensions.Options.IOptions<ServerOptions>>(
            Microsoft.Extensions.Options.Options.Create(opts));

        // Stub the PBI dependency graph — none of these are exercised by
        // the auth/healthz tests, but DI needs to resolve them when the
        // MCP endpoint is constructed.
        builder.Services.AddSingleton<ITokenProvider, StubTokenProvider>();
        builder.Services.AddSingleton<ITomServerFactory, StubTomServerFactory>();
        builder.Services.AddSingleton<IConnectionManager, ConnectionManager>();
        builder.Services.AddSingleton<IModelingService, ModelingService>();
        builder.Services.AddSingleton<PbiModelingMcp.Audit.IAuditLogger, NullAuditLogger>();
        builder.Services.AddSingleton<PbiModelingMcp.Audit.IBackupWriter, NullBackupWriter>();
        builder.Services.AddHttpClient<IPowerBiRestClient, PowerBiRestClient>();

        _app = HttpServerHost.Build(builder, opts);
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client?.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Healthz_NoAuth_Returns200()
    {
        var resp = await _client!.GetAsync("/healthz");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Mcp_NoAuth_Returns401()
    {
        var resp = await _client!.PostAsync("/", new StringContent(""));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // We use a custom X-Api-Key header rather than Authorization:
        // Bearer specifically so MCP clients (notably VS Code's) do not
        // fall into OAuth protected-resource discovery on a 401. See
        // ApiKeyAuthMiddleware for the rationale.
        resp.Headers.WwwAuthenticate.Should().BeEmpty();
    }

    [Fact]
    public async Task Mcp_WrongKey_Returns401()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(""),
        };
        req.Headers.Add(ApiKeyAuthMiddleware.HeaderName, "this-is-not-the-right-key-padding-padding");
        var resp = await _client!.SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Mcp_ValidKey_PassesGate()
    {
        // Empty body still routes through the API-key check; the MCP handler
        // will refuse the malformed payload (4xx that is not 401). The point
        // here is that the auth gate let it through.
        using var req = new HttpRequestMessage(HttpMethod.Post, "/")
        {
            Content = new StringContent(""),
        };
        req.Headers.Add(ApiKeyAuthMiddleware.HeaderName, ValidToken);
        var resp = await _client!.SendAsync(req);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
    }

    private sealed class StubTokenProvider : ITokenProvider
    {
        public ValueTask<string> GetPowerBiTokenAsync(System.Threading.CancellationToken ct)
            => ValueTask.FromResult("stub-token");
    }

    private sealed class StubTomServerFactory : ITomServerFactory
    {
        public Task<Microsoft.AnalysisServices.Tabular.Server> OpenAsync(ConnectionDescriptor descriptor, System.Threading.CancellationToken ct)
            => throw new System.NotSupportedException("Integration tests do not exercise the TOM path.");
    }

    private sealed class NullAuditLogger : PbiModelingMcp.Audit.IAuditLogger
    {
        public Task LogAsync(PbiModelingMcp.Audit.AuditEvent evt, System.Threading.CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullBackupWriter : PbiModelingMcp.Audit.IBackupWriter
    {
        public Task<string> SnapshotAsync(ConnectionDescriptor descriptor, Microsoft.AnalysisServices.Tabular.Database db, string action, System.Threading.CancellationToken ct)
            => Task.FromResult("/dev/null");
    }
}
