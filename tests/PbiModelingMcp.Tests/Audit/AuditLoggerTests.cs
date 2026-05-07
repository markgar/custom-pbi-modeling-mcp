using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PbiModelingMcp.Audit;
using PbiModelingMcp.Configuration;
using PbiModelingMcp.Connection;
using Xunit;

namespace PbiModelingMcp.Tests.Audit;

public sealed class AuditLoggerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly AuditLogger _logger;

    public AuditLoggerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pbi-mcp-audit-tests-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new ServerOptions { AuditDir = _tempDir });
        _logger = new AuditLogger(opts, NullLogger<AuditLogger>.Instance);
    }

    public void Dispose()
    {
        _logger.Dispose();
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task LogAsync_WritesOneJsonLinePerEvent()
    {
        var evt = MakeEvent(phase: "pre", action: "add_measure");

        await _logger.LogAsync(evt, CancellationToken.None);

        var lines = ReadTodayLog();
        lines.Should().HaveCount(1);
        var doc = JsonDocument.Parse(lines[0]);
        doc.RootElement.GetProperty("schema").GetInt32().Should().Be(AuditSchema.Version);
        doc.RootElement.GetProperty("phase").GetString().Should().Be("pre");
        doc.RootElement.GetProperty("action").GetString().Should().Be("add_measure");
    }

    [Fact]
    public async Task LogAsync_FilenameUsesUtcDateOfTimestamp()
    {
        // Force a timestamp on a different UTC date than "now" to confirm the
        // file name comes from the event's ts, not from System time.
        var evt = MakeEvent(timestampUtc: new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc));

        await _logger.LogAsync(evt, CancellationToken.None);

        var expected = Path.Combine(_tempDir, "audit", "audit-2024-06-15.log");
        File.Exists(expected).Should().BeTrue();
    }

    [Fact]
    public async Task LogAsync_AppendsRatherThanTruncates()
    {
        var t = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        await _logger.LogAsync(MakeEvent(action: "first", timestampUtc: t), CancellationToken.None);
        await _logger.LogAsync(MakeEvent(action: "second", timestampUtc: t), CancellationToken.None);

        var lines = ReadTodayLog(t);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("\"action\":\"first\"");
        lines[1].Should().Contain("\"action\":\"second\"");
    }

    [Fact]
    public async Task LogAsync_OmitsNullOptionalFields()
    {
        // pre event has no outcome / durationMs / error / backupPath — those
        // properties should not appear in the JSON at all.
        var evt = MakeEvent(phase: "pre");

        await _logger.LogAsync(evt, CancellationToken.None);

        var line = ReadTodayLog()[0];
        line.Should().NotContain("\"outcome\"");
        line.Should().NotContain("\"durationMs\"");
        line.Should().NotContain("\"error\"");
        line.Should().NotContain("\"backupPath\"");
    }

    [Fact]
    public async Task LogAsync_PostEventIncludesOutcomeAndDuration()
    {
        var evt = MakeEvent(
            phase: "post",
            outcome: "applied",
            durationMs: 42,
            backupPath: "/tmp/x.bim");

        await _logger.LogAsync(evt, CancellationToken.None);

        var doc = JsonDocument.Parse(ReadTodayLog()[0]);
        doc.RootElement.GetProperty("outcome").GetString().Should().Be("applied");
        doc.RootElement.GetProperty("durationMs").GetInt64().Should().Be(42);
        doc.RootElement.GetProperty("backupPath").GetString().Should().Be("/tmp/x.bim");
    }

    [Fact]
    public async Task LogAsync_WriteIsAtomicAcrossConcurrentCalls()
    {
        // 50 parallel writes should yield 50 valid JSONL lines, none interleaved.
        var t = new DateTime(2024, 6, 15, 12, 0, 0, DateTimeKind.Utc);
        var tasks = Enumerable.Range(0, 50)
            .Select(i => _logger.LogAsync(MakeEvent(action: $"a{i}", timestampUtc: t), CancellationToken.None))
            .ToArray();

        await Task.WhenAll(tasks);

        var lines = ReadTodayLog(t);
        lines.Should().HaveCount(50);
        foreach (var line in lines)
        {
            var act = () => JsonDocument.Parse(line);
            act.Should().NotThrow($"every line should parse as JSON: <<{line}>>");
        }
    }

    private static AuditEvent MakeEvent(
        string phase = "pre",
        string action = "test_action",
        string? outcome = null,
        long? durationMs = null,
        string? backupPath = null,
        DateTime? timestampUtc = null)
        => new(
            Schema: AuditSchema.Version,
            TimestampUtc: timestampUtc ?? DateTime.UtcNow,
            Phase: phase,
            Action: action,
            Descriptor: AuditDescriptor.From(new ConnectionDescriptor("ws", "ds")),
            Args: null,
            DryRun: false,
            Actor: "tester",
            Outcome: outcome,
            DurationMs: durationMs,
            BackupPath: backupPath);

    private List<string> ReadTodayLog(DateTime? day = null)
    {
        var d = day ?? DateTime.UtcNow;
        var path = Path.Combine(_tempDir, "audit", $"audit-{d:yyyy-MM-dd}.log");
        return File.ReadAllLines(path).ToList();
    }
}
