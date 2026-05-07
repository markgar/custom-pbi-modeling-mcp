using System.Data.Common;
using FluentAssertions;
using PbiModelingMcp.Connection;
using Xunit;

namespace PbiModelingMcp.Tests.Connection;

public class TomServerFactoryTests
{
    // Shape-only token; the real provider returns a JWT.
    private const string FakeToken = "eyJhbGciOiJSUzI1NiJ9.fake.token";

    [Fact]
    public void BuildConnectionString_EmitsAllRequiredKeys()
    {
        var cs = TomServerFactory.BuildConnectionString(
            new ConnectionDescriptor("Sample", "Sample Model"),
            FakeToken);

        var b = new DbConnectionStringBuilder { ConnectionString = cs };
        b["Provider"].ToString().Should().Be("MSOLAP");
        b["Data Source"].ToString().Should().Be("powerbi://api.powerbi.com/v1.0/myorg/Sample");
        b["Initial Catalog"].ToString().Should().Be("Sample Model");
        b["Password"].ToString().Should().Be(FakeToken);
        // The token already encodes the principal; no User ID is set.
        b.ContainsKey("User ID").Should().BeFalse();
    }

    [Fact]
    public void BuildConnectionString_UrlEncodesWorkspaceNameWithSpaces()
    {
        var cs = TomServerFactory.BuildConnectionString(
            new ConnectionDescriptor("My Workspace", "ds"),
            FakeToken);

        var b = new DbConnectionStringBuilder { ConnectionString = cs };
        b["Data Source"].ToString().Should().Be("powerbi://api.powerbi.com/v1.0/myorg/My%20Workspace");
    }

    [Fact]
    public void BuildConnectionString_QuotesValuesContainingSemicolon()
    {
        // A token (or, in the wild, a dataset name) with a semicolon must
        // not be able to inject a new connection-string key.
        const string nasty = "abc;Integrated Security=true";

        var cs = TomServerFactory.BuildConnectionString(
            new ConnectionDescriptor("ws", "ds"),
            nasty);

        // Round-trip through the parser: the value survives intact, and no
        // unexpected key was introduced.
        var b = new DbConnectionStringBuilder { ConnectionString = cs };
        b["Password"].ToString().Should().Be(nasty);
        b.ContainsKey("Integrated Security").Should().BeFalse();
    }

    [Fact]
    public void BuildConnectionString_QuotesDatasetWithEqualsSign()
    {
        var cs = TomServerFactory.BuildConnectionString(
            new ConnectionDescriptor("ws", "weird=name;with chars"),
            FakeToken);

        var b = new DbConnectionStringBuilder { ConnectionString = cs };
        b["Initial Catalog"].ToString().Should().Be("weird=name;with chars");
    }

    [Fact]
    public void BuildConnectionString_NullDescriptor_Throws()
    {
        Action act = () => TomServerFactory.BuildConnectionString(null!, FakeToken);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void BuildConnectionString_NullToken_Throws()
    {
        Action act = () => TomServerFactory.BuildConnectionString(
            new ConnectionDescriptor("ws", "ds"), null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
