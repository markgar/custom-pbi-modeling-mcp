using FluentAssertions;
using PbiModelingMcp.Configuration;
using PbiModelingMcp.Http;
using Xunit;

namespace PbiModelingMcp.IntegrationTests;

/// <summary>
/// Pure unit tests for <see cref="HttpTransportValidation"/>. Lives in
/// the integration-tests project because <see cref="HttpServerHost"/>
/// and friends are wired up here, and keeping all HTTP-shape tests in
/// one project avoids InternalsVisibleTo sprawl.
/// </summary>
public class HttpTransportValidationTests
{
    private const string ValidToken = "test-token-of-sufficient-length-1234"; // 36 chars

    [Fact]
    public void Loopback_AuthOn_DefaultPasses()
    {
        var opts = new ServerOptions
        {
            Transport = "http",
            HttpHost = "127.0.0.1",
            HttpAuthToken = ValidToken,
        };

        var act = () => HttpTransportValidation.ValidateForHttp(opts);
        act.Should().NotThrow();
    }

    [Fact]
    public void Loopback_AuthDisabled_AllowsMissingToken()
    {
        var opts = new ServerOptions
        {
            Transport = "http",
            HttpHost = "127.0.0.1",
            HttpDisableAuth = true,
        };

        var act = () => HttpTransportValidation.ValidateForHttp(opts);
        act.Should().NotThrow();
    }

    [Fact]
    public void NonLoopback_NoAllowInsecure_Refused()
    {
        var opts = new ServerOptions
        {
            Transport = "http",
            HttpHost = "0.0.0.0",
            HttpAuthToken = ValidToken,
        };

        var act = () => HttpTransportValidation.ValidateForHttp(opts);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*non-loopback without TLS*");
    }

    [Fact]
    public void HttpListenAllInterfaces_SkipsLoopbackAndIPParseChecks()
    {
        // Platform-managed (App Service / ACA) shape: TLS termination is
        // upstream, the platform picks the bind address. Validation must
        // not reject "non-loopback bind without HttpAllowInsecure" here,
        // and must not require HttpHost to be a parsable IPAddress.
        var opts = new ServerOptions
        {
            Transport = "http",
            HttpHost = "ignored-by-platform-mode",
            HttpAuthToken = ValidToken,
            HttpListenAllInterfaces = true,
        };

        var act = () => HttpTransportValidation.ValidateForHttp(opts);
        act.Should().NotThrow();
    }

    [Fact]
    public void HttpListenAllInterfaces_AuthDisabled_StillAllowedButBannerScreams()
    {
        // We don't refuse to start; the operator may genuinely want auth
        // off for an ephemeral demo behind a private platform front-door.
        // What we DO commit to is the loud banner.
        var opts = new ServerOptions
        {
            Transport = "http",
            HttpListenAllInterfaces = true,
            HttpDisableAuth = true,
        };

        var act = () => HttpTransportValidation.ValidateForHttp(opts);
        act.Should().NotThrow();

        var banner = HttpTransportValidation.BuildStartupBanner(opts, "actor", isLoopback: false);
        banner.Should().Contain("AUTH DISABLED")
            .And.Contain("platform-managed")
            .And.Contain("Do NOT run this configuration anywhere reachable");
    }

    [Fact]
    public void ResolveListenPort_HonorsPortEnvVar_WhenAllInterfaces()
    {
        // Round-trip via Environment.SetEnvironmentVariable so the resolver
        // sees the value the way App Service / ACA would inject it.
        var prev = Environment.GetEnvironmentVariable("PORT");
        try
        {
            Environment.SetEnvironmentVariable("PORT", "8080");
            var opts = new ServerOptions
            {
                Transport = "http",
                HttpListenAllInterfaces = true,
                HttpPort = 5000,
            };
            opts.ResolveListenPort().Should().Be(8080);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PORT", prev);
        }
    }

    [Fact]
    public void ResolveListenPort_IgnoresPortEnvVar_WhenSelfHosted()
    {
        var prev = Environment.GetEnvironmentVariable("PORT");
        try
        {
            Environment.SetEnvironmentVariable("PORT", "8080");
            var opts = new ServerOptions
            {
                Transport = "http",
                HttpListenAllInterfaces = false,
                HttpPort = 5000,
            };
            opts.ResolveListenPort().Should().Be(5000);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PORT", prev);
        }
    }
}
