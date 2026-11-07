using System.Text.Json;
using AgentRelay.Agent;
using AgentRelay.Core;
using AgentTaskStatus = AgentRelay.Core.TaskStatus;
using Xunit;

namespace AgentRelay.Agent.Tests;

public sealed class TaskStoreTests
{
    [Fact]
    public void Submit_same_key_and_request_returns_same_task_id()
    {
        using var directory = new TemporaryDirectory();
        var store = new TaskStore(directory.Path);
        var request = Request();

        var first = store.Submit(request);
        var second = store.Submit(request);

        Assert.Equal(TaskSubmissionDisposition.Accepted, first.Disposition);
        Assert.Equal(TaskSubmissionDisposition.AlreadyAccepted, second.Disposition);
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.Equal(AgentTaskStatus.Queued, second.Status.Status);
    }

    [Fact]
    public async Task Parallel_identical_submissions_create_one_task_record()
    {
        using var directory = new TemporaryDirectory();
        var store = new TaskStore(directory.Path);
        var request = Request();

        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => store.Submit(request))));

        Assert.Single(results.Select(result => result.TaskId).Distinct());
        Assert.Single(results, result => result.Disposition == TaskSubmissionDisposition.Accepted);
        Assert.Single(Directory.GetFiles(directory.Path, "*.json"));
    }

    [Fact]
    public void Submit_same_key_with_different_request_returns_conflict_without_mutation()
    {
        using var directory = new TemporaryDirectory();
        var store = new TaskStore(directory.Path);
        var accepted = store.Submit(Request());
        var before = File.ReadAllText(Directory.GetFiles(directory.Path, "*.json").Single());

        var conflict = store.Submit(Request(taskId: "task-2", branch: "task/43"));

        Assert.Equal(TaskSubmissionDisposition.Conflict, conflict.Disposition);
        Assert.Equal(accepted.TaskId, conflict.TaskId);
        Assert.Equal(before, File.ReadAllText(Directory.GetFiles(directory.Path, "*.json").Single()));
    }

    [Fact]
    public void FindByIdempotencyKey_reconciles_a_lost_acknowledgment()
    {
        using var directory = new TemporaryDirectory();
        var request = Request();
        new TaskStore(directory.Path).Submit(request);

        var recovered = new TaskStore(directory.Path).FindByIdempotencyKey(request.IdempotencyKey);

        Assert.NotNull(recovered);
        Assert.Equal(TaskSubmissionDisposition.AlreadyAccepted, recovered.Disposition);
        Assert.Equal("task-1", recovered.TaskId);
    }

    [Fact]
    public void WriteStatus_and_WriteReport_reject_mismatched_identity()
    {
        using var directory = new TemporaryDirectory();
        var store = new TaskStore(directory.Path);
        var accepted = store.Submit(Request());

        Assert.Throws<InvalidOperationException>(() => store.WriteStatus(new TaskStatusRecord(
            TaskContract.SchemaVersion, "other-task", "key-1", "device-a", AgentTaskStatus.Running, DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidOperationException>(() => store.WriteStatus(new TaskStatusRecord(
            TaskContract.SchemaVersion, "task-1", "key-1", "other-device", AgentTaskStatus.Running, DateTimeOffset.UtcNow)));
        Assert.Throws<InvalidOperationException>(() => store.WriteReport(Report(issueUrl: "https://github.com/acme/project/issues/43")));
        Assert.Equal(AgentTaskStatus.Queued, store.FindByTaskId(accepted.TaskId)!.Status.Status);
    }

    [Fact]
    public void WriteStatus_and_WriteReport_persist_matching_task_identity()
    {
        using var directory = new TemporaryDirectory();
        var store = new TaskStore(directory.Path);
        store.Submit(Request());
        var status = new TaskStatusRecord(TaskContract.SchemaVersion, "task-1", "key-1", "device-a",
            AgentTaskStatus.Running, DateTimeOffset.UtcNow);

        store.WriteStatus(status);
        store.WriteReport(Report());

        var persisted = store.FindByTaskId("task-1");
        Assert.NotNull(persisted);
        Assert.Equal(AgentTaskStatus.Running, persisted.Status.Status);
        Assert.Equal("https://github.com/acme/project/issues/42", persisted.Report!.IssueUrl);
    }

    [Fact]
    public void Store_fails_closed_on_corrupt_or_unsupported_record()
    {
        using var directory = new TemporaryDirectory();
        var store = new TaskStore(directory.Path);
        store.Submit(Request());
        var path = Directory.GetFiles(directory.Path, "*.json").Single();
        File.WriteAllText(path, "{");

        Assert.ThrowsAny<JsonException>(() => store.FindByTaskId("task-1"));
        Assert.ThrowsAny<JsonException>(() => store.Submit(Request()));
        File.WriteAllText(path, """{"schema_version":"agentrelay-task-store/v9"}""");
        Assert.ThrowsAny<JsonException>(() => store.FindByIdempotencyKey("key-1"));
    }

    [Fact]
    public void Untrusted_keys_task_ids_and_issue_values_never_form_paths()
    {
        using var directory = new TemporaryDirectory();
        var hostileKey = "../../../../outside\\..\\key";
        var request = Request(taskId: "../../outside", key: hostileKey);
        var store = new TaskStore(directory.Path);

        store.Submit(request);

        Assert.All(Directory.GetFiles(directory.Path), file =>
            Assert.StartsWith(System.IO.Path.GetFullPath(directory.Path) + System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(directory.Path)!, "outside")));
    }

    private static TaskRequest Request(string taskId = "task-1", string key = "key-1", string branch = "task/42") => new(
        TaskContract.SchemaVersion, taskId, key, "https://github.com/acme/project/issues/42", AgentRelayRole.Worker,
        "device-a", "C:/repos/project", "C:/work/task-1", branch, 1);

    private static TaskReport Report(string issueUrl = "https://github.com/acme/project/issues/42") => new(
        TaskContract.SchemaVersion, "task-1", "key-1", issueUrl, AgentRelayRole.Worker, "device-a", AgentTaskStatus.Succeeded,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "task/42", "abc123", ["tests passed"], [], [], null);

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
