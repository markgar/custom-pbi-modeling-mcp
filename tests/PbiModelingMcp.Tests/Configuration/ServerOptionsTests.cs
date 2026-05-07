using FluentAssertions;
using PbiModelingMcp.Configuration;
using Xunit;

namespace PbiModelingMcp.Tests.Configuration;

public class ServerOptionsTests
{
    [Fact]
    public void ResolveAuditDir_DefaultsToHomeDotDir_WhenUnset()
    {
        var o = new ServerOptions { AuditDir = null };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        o.ResolveAuditDir().Should().Be(Path.Combine(home, ".pbi-modeling-mcp"));
    }

    [Fact]
    public void ResolveAuditDir_ExpandsTildeAndEnvVars()
    {
        var o = new ServerOptions { AuditDir = "~/audit" };
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        o.ResolveAuditDir().Should().Be(Path.Combine(home, "audit"));
    }

    [Fact]
    public void ResolveActor_FallsBackToUserAtHost()
    {
        var o = new ServerOptions { Actor = null };
        o.ResolveActor().Should().Contain("@");
    }

    [Fact]
    public void ResolveActor_UsesExplicitWhenSet()
    {
        var o = new ServerOptions { Actor = "claude-desktop" };
        o.ResolveActor().Should().Be("claude-desktop");
    }
}
