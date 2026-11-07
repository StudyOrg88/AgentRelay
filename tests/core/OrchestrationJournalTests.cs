using System.Text.Json;
using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Core.Tests;

public sealed class OrchestrationJournalTests
{
    [Fact]
    public void Load_restores_dispatch_counts_cursors_intents_and_active_tasks_after_restart()
    {
        using var directory = new TemporaryDirectory();
        var snapshot = Snapshot();

        new OrchestrationJournal(directory.Path).Save(snapshot);
        var restored = new OrchestrationJournal(directory.Path).Load();

        Assert.Equal(snapshot.SchemaVersion, restored.SchemaVersion);
        Assert.Equal(snapshot.Revision, restored.Revision);
        Assert.Equal(snapshot.AcceptedDispatchCounts, restored.AcceptedDispatchCounts);
        Assert.Equal(snapshot.Cursors, restored.Cursors);
        Assert.Equal(snapshot.PendingIntents, restored.PendingIntents);
        Assert.Equal(snapshot.ActiveTasks, restored.ActiveTasks);
        Assert.Equal(snapshot.Checkpoints, restored.Checkpoints);
        Assert.Equal(3, Assert.Single(restored.AcceptedDispatchCounts).Count);
        Assert.Equal("device-b", Assert.Single(restored.Cursors).DeviceId);
        Assert.Equal("key-1", Assert.Single(restored.PendingIntents).IdempotencyKey);
        Assert.Equal("task-1", Assert.Single(restored.ActiveTasks).TaskId);
    }

    [Fact]
    public void Load_falls_back_to_valid_backup_when_primary_is_malformed_or_unsupported()
    {
        using var directory = new TemporaryDirectory();
        var journal = new OrchestrationJournal(directory.Path);
        var previous = Snapshot(revision: 1);
        journal.Save(previous);
        journal.Save(Snapshot(revision: 2));
        var primary = System.IO.Path.Combine(directory.Path, "snapshot.json");

        File.WriteAllText(primary, "{");
        Assert.Equal(1, journal.Load().Revision);
        File.WriteAllText(primary, JsonSerializer.Serialize(Snapshot(revision: 2) with { SchemaVersion = "v9" }));
        Assert.Equal(1, journal.Load().Revision);
    }

    [Fact]
    public void Load_throws_when_primary_and_backup_are_invalid()
    {
        using var directory = new TemporaryDirectory();
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "snapshot.json"), "{");
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "snapshot.json.bak"), "not-json");

        Assert.ThrowsAny<JsonException>(() => new OrchestrationJournal(directory.Path).Load());
    }

    [Fact]
    public void Load_ignores_interrupted_temporary_write_and_keeps_previous_snapshot()
    {
        using var directory = new TemporaryDirectory();
        var journal = new OrchestrationJournal(directory.Path);
        journal.Save(Snapshot());
        File.WriteAllText(System.IO.Path.Combine(directory.Path, "snapshot.json.tmp-interrupted"), "{");

        Assert.Equal(1, journal.Load().Revision);
    }

    [Fact]
    public void AcquireSingleInstanceLock_rejects_a_second_holder()
    {
        using var directory = new TemporaryDirectory();
        var journal = new OrchestrationJournal(directory.Path);
        using var lease = journal.AcquireSingleInstanceLock();

        Assert.Throws<IOException>(() => journal.AcquireSingleInstanceLock());
    }

    [Fact]
    public void Save_rejects_negative_counts_and_unknown_schema()
    {
        using var directory = new TemporaryDirectory();
        var journal = new OrchestrationJournal(directory.Path);

        Assert.Throws<ArgumentException>(() => journal.Save(Snapshot() with
        {
            AcceptedDispatchCounts = [new AcceptedDispatchCount("device-a", AgentRelayRole.Worker, -1)]
        }));
        Assert.Throws<ArgumentException>(() => journal.Save(Snapshot(revision: -1)));
        Assert.Throws<ArgumentException>(() => journal.Save(Snapshot() with { SchemaVersion = "v9" }));
    }

    private static OrchestrationJournalSnapshot Snapshot(long revision = 1) => new(
        "agentrelay-controller-state/v1", revision,
        [new AcceptedDispatchCount("device-a", AgentRelayRole.Worker, 3)],
        [new SchedulerCursor(AgentRelayRole.Reviewer, "device-b")],
        [new TaskDispatchIntent("https://github.com/acme/project/issues/42", AgentRelayRole.Worker,
            "device-a", "key-1", "hash-1")],
        [new ActiveTaskRecord("task-1", "key-1", "https://github.com/acme/project/issues/42",
            AgentRelayRole.Worker, "device-a", TaskStatus.Running, DateTimeOffset.UnixEpoch)],
        [new ControllerCheckpoint("https://github.com/acme/project/issues/42", "dispatching", DateTimeOffset.UnixEpoch)]);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
