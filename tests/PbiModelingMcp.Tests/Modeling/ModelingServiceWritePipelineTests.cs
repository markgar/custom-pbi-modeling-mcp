using FluentAssertions;
using Microsoft.AnalysisServices.Tabular;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PbiModelingMcp.Audit;
using PbiModelingMcp.Configuration;
using PbiModelingMcp.Connection;
using PbiModelingMcp.Modeling;
using Xunit;

namespace PbiModelingMcp.Tests.Modeling;

/// <summary>
/// Behavioural tests for <see cref="ModelingService"/>'s safety pipeline.
/// We can fully exercise the dry-run and pre-validation paths against an
/// in-memory TOM <see cref="Database"/>; the real <c>SaveChanges()</c> path
/// requires a live model and is covered by integration tests.
/// </summary>
public class ModelingServiceWritePipelineTests
{
    private static readonly ConnectionDescriptor Descriptor = new("ws", "ds");

    [Fact]
    public async Task AddMeasure_DryRun_ReturnsPreviewWithDiff_NoBackup()
    {
        var db = MakeDatabaseWithTable("Sales");
        var (svc, audit, backup) = MakeService(db);

        var result = await svc.AddMeasureAsync(
            new AddMeasureRequest("Sales", "Revenue", "1", DryRun: true),
            Descriptor,
            CancellationToken.None);

        result.Outcome.Should().Be("preview");
        result.Action.Should().Be("add_measure");
        result.Table.Should().Be("Sales");
        result.Measure.Should().Be("Revenue");
        result.DiffBefore.Should().NotBeNullOrWhiteSpace();
        result.DiffAfter.Should().NotBeNullOrWhiteSpace();
        result.DiffAfter.Should().NotBe(result.DiffBefore);
        result.BackupPath.Should().BeNull("backups are only taken on real writes");

        // Audit: one pre + one post (preview).
        audit.Events.Should().HaveCount(2);
        audit.Events[0].Phase.Should().Be("pre");
        audit.Events[1].Phase.Should().Be("post");
        audit.Events[1].Outcome.Should().Be("preview");
        audit.Events[1].DryRun.Should().BeTrue();

        // No backup file requested.
        backup.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task AddMeasure_Duplicate_ThrowsAndAuditsError()
    {
        var db = MakeDatabaseWithTable("Sales");
        db.Model!.Tables.Find("Sales")!.Measures.Add(new Measure { Name = "Revenue", Expression = "1" });
        var (svc, audit, _) = MakeService(db);

        var act = () => svc.AddMeasureAsync(
            new AddMeasureRequest("Sales", "Revenue", "2", DryRun: true),
            Descriptor,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already exists*");

        // Audit: pre + post(error)
        audit.Events.Should().HaveCount(2);
        audit.Events[1].Outcome.Should().Be("error");
        audit.Events[1].Error.Should().NotBeNull();
        audit.Events[1].Error!.Type.Should().Be(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task UpdateMeasure_DryRun_AppliesAndUndoes()
    {
        var db = MakeDatabaseWithTable("Sales");
        db.Model!.Tables.Find("Sales")!.Measures.Add(new Measure
        {
            Name = "Revenue",
            Expression = "1",
            FormatString = "0",
        });
        var (svc, audit, _) = MakeService(db);

        var result = await svc.UpdateMeasureAsync(
            new UpdateMeasureRequest("Sales", "Revenue", Dax: "2", DryRun: true),
            Descriptor,
            CancellationToken.None);

        result.Outcome.Should().Be("preview");
        // The dry-run pipeline serializes the table before *and* after apply,
        // so the diff must reflect the intended change.
        result.DiffBefore.Should().Contain("\"expression\": \"1\"");
        result.DiffAfter.Should().Contain("\"expression\": \"2\"");

        audit.Events.Should().HaveCount(2);
        audit.Events[1].Outcome.Should().Be("preview");
    }

    [Fact]
    public async Task UpdateMeasure_Missing_ThrowsAndAudits()
    {
        var db = MakeDatabaseWithTable("Sales");
        var (svc, audit, _) = MakeService(db);

        var act = () => svc.UpdateMeasureAsync(
            new UpdateMeasureRequest("Sales", "Missing", Dax: "1", DryRun: true),
            Descriptor,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
        audit.Events.Should().HaveCount(2);
        audit.Events[1].Outcome.Should().Be("error");
    }

    [Fact]
    public async Task DeleteMeasure_DryRun_ReturnsPreview()
    {
        var db = MakeDatabaseWithTable("Sales");
        db.Model!.Tables.Find("Sales")!.Measures.Add(new Measure { Name = "Revenue", Expression = "1" });
        var (svc, _, backup) = MakeService(db);

        var result = await svc.DeleteMeasureAsync(
            new DeleteMeasureRequest("Sales", "Revenue", DryRun: true),
            Descriptor,
            CancellationToken.None);

        result.Outcome.Should().Be("preview");
        result.DiffBefore.Should().NotBeNullOrWhiteSpace();
        result.DiffAfter.Should().NotBeNullOrWhiteSpace();
        backup.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RunWriteAsync_TableMissing_ThrowsBeforeAudit()
    {
        // pre-validate runs before audit pre — so a missing table should
        // produce zero audit events. That keeps the log noise-free.
        var db = MakeDatabaseWithTable("Other");
        var (svc, audit, _) = MakeService(db);

        var act = () => svc.AddMeasureAsync(
            new AddMeasureRequest("Sales", "Revenue", "1", DryRun: true),
            Descriptor,
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*Table 'Sales' not found*");
        audit.Events.Should().BeEmpty("pre-validate runs before audit");
    }

    [Fact]
    public void SanitizeArgs_RedactsCredentialKeys()
    {
        var args = new Dictionary<string, object?>
        {
            ["table"] = "Sales",
            ["name"] = "Revenue",
            ["clientSecret"] = "shh",
            ["password"] = "shh",
            ["accessToken"] = "shh",
            ["myCredential"] = "shh",
            ["apiKey"] = "shh",
        };

        var scrubbed = ModelingService.SanitizeArgs(args);

        scrubbed["table"].Should().Be("Sales");
        scrubbed["name"].Should().Be("Revenue");
        scrubbed["clientSecret"].Should().Be("[REDACTED]");
        scrubbed["password"].Should().Be("[REDACTED]");
        scrubbed["accessToken"].Should().Be("[REDACTED]");
        scrubbed["myCredential"].Should().Be("[REDACTED]");
        scrubbed["apiKey"].Should().Be("[REDACTED]");
    }

    [Fact]
    public void SanitizeArgs_NoSecretKeys_ReturnsSameInstance()
    {
        var args = new Dictionary<string, object?>
        {
            ["table"] = "Sales",
            ["name"] = "Revenue",
            ["dax"] = "1",
        };

        var scrubbed = ModelingService.SanitizeArgs(args);
        scrubbed.Should().BeSameAs(args, "no allocation when nothing needs scrubbing");
    }

    private static (ModelingService svc, RecordingAudit audit, RecordingBackup backup) MakeService(Database db)
    {
        var conn = new FakeConnectionManager(db);
        var audit = new RecordingAudit();
        var backup = new RecordingBackup();
        var opts = Options.Create(new ServerOptions());
        var svc = new ModelingService(conn, audit, backup, opts, NullLogger<ModelingService>.Instance);
        return (svc, audit, backup);
    }

    private static Database MakeDatabaseWithTable(string tableName)
    {
        var db = new Database("UnitTestDb")
        {
            ID = "UnitTestDb",
            CompatibilityLevel = 1500,
            Name = "UnitTestDb",
        };
        db.Model = new Model { Name = "Model" };
        db.Model.Tables.Add(new Table { Name = tableName });
        return db;
    }

    private sealed class FakeConnectionManager : IConnectionManager
    {
        private readonly Database _db;
        public FakeConnectionManager(Database db) => _db = db;

        public async Task<T> WithServerAsync<T>(
            ConnectionDescriptor descriptor,
            Func<Server, Database, CancellationToken, Task<T>> body,
            CancellationToken ct)
        {
            // ModelingService body never touches Server — only Database. A
            // throwaway instance is sufficient. Owning it here matches the
            // production lifecycle (using-disposed at end of WithServerAsync).
            using var server = new Server();
            return await body(server, _db, ct).ConfigureAwait(false);
        }
    }

    private sealed class RecordingAudit : IAuditLogger
    {
        public List<AuditEvent> Events { get; } = new();
        public Task LogAsync(AuditEvent evt, CancellationToken ct)
        {
            Events.Add(evt);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingBackup : IBackupWriter
    {
        public List<(ConnectionDescriptor Desc, string Action)> Calls { get; } = new();
        public Task<string> SnapshotAsync(
            ConnectionDescriptor descriptor, Database db, string action, CancellationToken ct)
        {
            Calls.Add((descriptor, action));
            return Task.FromResult($"/tmp/{action}.bim");
        }
    }
}
