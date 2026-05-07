using FluentAssertions;
using PbiModelingMcp;
using Serilog.Events;
using Xunit;

namespace PbiModelingMcp.Tests;

public class LogLevelParserTests
{
    [Theory]
    [InlineData("Verbose", LogEventLevel.Verbose)]
    [InlineData("trace", LogEventLevel.Verbose)]
    [InlineData("0", LogEventLevel.Verbose)]
    [InlineData("Debug", LogEventLevel.Debug)]
    [InlineData("1", LogEventLevel.Debug)]
    [InlineData("Information", LogEventLevel.Information)]
    [InlineData("info", LogEventLevel.Information)]
    [InlineData("2", LogEventLevel.Information)]
    [InlineData("Warning", LogEventLevel.Warning)]
    [InlineData("warn", LogEventLevel.Warning)]
    [InlineData("3", LogEventLevel.Warning)]
    [InlineData("Error", LogEventLevel.Error)]
    [InlineData("4", LogEventLevel.Error)]
    [InlineData("Fatal", LogEventLevel.Fatal)]
    [InlineData("critical", LogEventLevel.Fatal)]
    [InlineData("5", LogEventLevel.Fatal)]
    public void Parse_KnownTokens_AreRecognized(string raw, LogEventLevel expected)
    {
        var level = LogLevelParser.Parse(raw, out var recognized);
        level.Should().Be(expected);
        recognized.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrEmpty_DefaultsToInformationAndIsRecognized(string? raw)
    {
        var level = LogLevelParser.Parse(raw, out var recognized);
        level.Should().Be(LogEventLevel.Information);
        recognized.Should().BeTrue("blank input means 'use the default', not 'typo'");
    }

    [Theory]
    [InlineData("Debugg")]
    [InlineData("informational")]
    [InlineData("very-verbose")]
    [InlineData("9")]
    public void Parse_UnknownTokens_DefaultButReportNotRecognized(string raw)
    {
        var level = LogLevelParser.Parse(raw, out var recognized);
        level.Should().Be(LogEventLevel.Information);
        recognized.Should().BeFalse(
            "operator typos must be visible — caller is expected to surface a warning");
    }
}
