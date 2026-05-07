using FluentAssertions;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PbiModelingMcp.Audit;
using PbiModelingMcp.Configuration;
using PbiModelingMcp.Connection;
using Xunit;

namespace PbiModelingMcp.Tests.Audit;

public sealed class BackupWriterTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BackupWriter _writer;

    public BackupWriterTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "pbi-mcp-backup-tests-" + Guid.NewGuid().ToString("N"));
        var opts = Options.Create(new ServerOptions { AuditDir = _tempDir });
        _writer = new BackupWriter(opts, NullLogger<BackupWriter>.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task SnapshotAsync_WritesTmslUnderWorkspaceAndDataset()
    {
        using var db = MakeMinimalDatabase();

        var path = await _writer.SnapshotAsync(
            new ConnectionDescriptor("My Workspace", "My Dataset"),
            db,
            "add_measure",
            CancellationToken.None);

        // Path: {AuditDir}/backups/{wsSlug}/{dsSlug}/{stamp}-{action}.bim
        path.Should().StartWith(Path.Combine(_tempDir, "backups", "My-Workspace", "My-Dataset"));
        path.Should().EndWith("-add_measure.bim");
        File.Exists(path).Should().BeTrue();
        var contents = await File.ReadAllTextAsync(path);
        contents.Should().NotBeNullOrWhiteSpace();
        contents.Should().StartWith("{");
    }

    [Theory]
    [InlineData("My Workspace", "My-Workspace")]
    [InlineData("ws/with/slashes", "ws-with-slashes")]
    [InlineData("ws:with*chars?", "ws-with-chars")]
    [InlineData("---trim-edges---", "trim-edges")]
    [InlineData("under_score-and-dash", "under_score-and-dash")]
    [InlineData("../etc/passwd", "etc-passwd")]
    [InlineData("a\\b", "a-b")]
    public async Task SnapshotAsync_SlugsWorkspaceAndDataset(string raw, string expectedSlug)
    {
        using var db = MakeMinimalDatabase();

        var path = await _writer.SnapshotAsync(
            new ConnectionDescriptor(raw, raw),
            db,
            "add_measure",
            CancellationToken.None);

        var dir = Path.GetDirectoryName(path)!;
        Path.GetFileName(dir).Should().Be(expectedSlug);
        Path.GetFileName(Path.GetDirectoryName(dir)!).Should().Be(expectedSlug);

        // Resolve fully so any unexpected '..' segments would manifest as
        // an escape from the backups root.
        var backupsRoot = Path.GetFullPath(Path.Combine(_tempDir, "backups"))
            + Path.DirectorySeparatorChar;
        Path.GetFullPath(path).Should().StartWith(backupsRoot,
            "slugging must prevent path traversal out of the backups root");
    }

    [Theory]
    // Path-traversal hardening: pathological inputs that would otherwise slug
    // to empty (and collapse a Path.Combine segment, putting the file at the
    // wrong level) fall back to '_'. The path-stays-inside-root assertion
    // pins the security property end-to-end.
    [InlineData("..")]
    [InlineData("/")]
    [InlineData("***")]
    [InlineData("---")]
    public async Task SnapshotAsync_PathologicalNames_FallBackToUnderscoreSlug(string raw)
    {
        using var db = MakeMinimalDatabase();

        var path = await _writer.SnapshotAsync(
            new ConnectionDescriptor(raw, raw),
            db,
            "add_measure",
            CancellationToken.None);

        var datasetDir = Path.GetDirectoryName(path)!;
        var workspaceDir = Path.GetDirectoryName(datasetDir)!;
        Path.GetFileName(datasetDir).Should().Be("_");
        Path.GetFileName(workspaceDir).Should().Be("_");

        var backupsRoot = Path.GetFullPath(Path.Combine(_tempDir, "backups"))
            + Path.DirectorySeparatorChar;
        Path.GetFullPath(path).Should().StartWith(backupsRoot);
    }

    [Fact]
    public async Task SnapshotAsync_FilenameStampUsesUtcCompactFormat()
    {
        using var db = MakeMinimalDatabase();

        var before = DateTime.UtcNow.AddSeconds(-1);
        var path = await _writer.SnapshotAsync(
            new ConnectionDescriptor("ws", "ds"),
            db,
            "delete_measure",
            CancellationToken.None);
        var after = DateTime.UtcNow.AddSeconds(1);

        // Filename shape: {yyyyMMddTHHmmssfffZ}-{action}.bim
        var name = Path.GetFileName(path);
        name.Should().MatchRegex(@"^\d{8}T\d{9}Z-delete_measure\.bim$");

        // And the timestamp parses inside the call window.
        var stamp = name[..16] + "Z";
        var parsed = DateTime.ParseExact(
            name[..(name.IndexOf('-', StringComparison.Ordinal))],
            "yyyyMMddTHHmmssfffZ",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal);
        parsed.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        // (locally-used variable kept to make the intent obvious)
        _ = stamp;
    }

    private static Database MakeMinimalDatabase()
    {
        // TOM's serializer requires a model with at least the standard
        // structures. A minimal in-memory database is enough for backup tests.
        var db = new Database("UnitTestDb")
        {
            ID = "UnitTestDb",
            CompatibilityLevel = 1500,
            Name = "UnitTestDb",
        };
        db.Model = new Model { Name = "Model" };
        return db;
    }
}
