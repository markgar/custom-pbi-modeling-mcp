using FluentAssertions;
using PbiModelingMcp.Configuration;
using Xunit;

namespace PbiModelingMcp.Tests.Configuration;

public class DotEnvTests
{
    [Fact]
    public void Parse_ReadsSimpleKeyValuePairs()
    {
        var pairs = DotEnv.Parse(new[]
        {
            "FOO=bar",
            "BAZ=qux",
        }).ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);

        pairs.Should().ContainKey("FOO").WhoseValue.Should().Be("bar");
        pairs.Should().ContainKey("BAZ").WhoseValue.Should().Be("qux");
    }

    [Fact]
    public void Parse_SkipsBlankAndCommentLines()
    {
        var pairs = DotEnv.Parse(new[]
        {
            "",
            "   ",
            "# this is a comment",
            "#KEY=ignored",
            "REAL=yes",
        }).ToList();

        pairs.Should().HaveCount(1);
        pairs[0].Key.Should().Be("REAL");
        pairs[0].Value.Should().Be("yes");
    }

    [Fact]
    public void Parse_SkipsLinesWithoutEqualsOrLeadingEquals()
    {
        var pairs = DotEnv.Parse(new[]
        {
            "no-equals-here",
            "=missing-key",
            "GOOD=ok",
        }).ToList();

        pairs.Should().ContainSingle();
        pairs[0].Key.Should().Be("GOOD");
    }

    [Theory]
    [InlineData("KEY=\"quoted value\"", "quoted value")]
    [InlineData("KEY='single quoted'", "single quoted")]
    [InlineData("KEY=unquoted", "unquoted")]
    [InlineData("KEY=  spaced  ", "spaced")]
    [InlineData("KEY=\"\"", "")]
    public void Parse_StripsMatchingQuotesAndTrims(string line, string expected)
    {
        var pairs = DotEnv.Parse(new[] { line }).ToList();
        pairs.Should().ContainSingle();
        pairs[0].Value.Should().Be(expected);
    }

    [Fact]
    public void Parse_PreservesEqualsSignsInsideValue()
    {
        // Only the first '=' is the separator; later ones are part of the value.
        var pairs = DotEnv.Parse(new[] { "KEY=a=b=c" }).ToList();
        pairs.Should().ContainSingle();
        pairs[0].Value.Should().Be("a=b=c");
    }

    [Fact]
    public void Parse_DoesNotStripMismatchedQuotes()
    {
        var pairs = DotEnv.Parse(new[] { "KEY=\"only-leading" }).ToList();
        pairs[0].Value.Should().Be("\"only-leading");
    }

    [Fact]
    public void Parse_NullInput_Throws()
    {
        var act = () => DotEnv.Parse(null!).ToList();
        act.Should().Throw<ArgumentNullException>();
    }
}
