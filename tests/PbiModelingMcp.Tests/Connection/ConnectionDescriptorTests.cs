using FluentAssertions;
using PbiModelingMcp.Connection;
using Xunit;

namespace PbiModelingMcp.Tests.Connection;

public class ConnectionDescriptorTests
{
    [Fact]
    public void ToString_FormatsAsWorkspaceSlashDataset()
    {
        var d = new ConnectionDescriptor("Sample", "Sample Model");
        d.ToString().Should().Be("Sample/Sample Model");
    }

    [Fact]
    public void Equality_IsValueBased()
    {
        var a = new ConnectionDescriptor("A", "B");
        var b = new ConnectionDescriptor("A", "B");
        var c = new ConnectionDescriptor("A", "C");

        a.Should().Be(b);
        a.Should().NotBe(c);
    }

    [Fact]
    public void CredentialName_DefaultsToNull()
    {
        var d = new ConnectionDescriptor("ws", "ds");
        d.CredentialName.Should().BeNull();
    }
}
