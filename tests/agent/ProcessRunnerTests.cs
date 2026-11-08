using System.Diagnostics;
using System.Text.Json;
using AgentRelay.Agent;
using AgentRelay.Core;
using AgentTaskStatus = AgentRelay.Core.TaskStatus;
using Xunit;

namespace AgentRelay.Agent.Tests;

public sealed class ProcessRunnerTests
{
    [Fact]
    public async Task Child_survives_caller_pipe_disconnect_and_logs_both_streams()
    {
        using var temp = new TemporaryDirectory();
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var request = Request("task-survive");
        store.Submit(request);
        var supervisor = new FakeSupervisor();
        var runner = new ProcessRunner(store, Path.Combine(temp.Path, "logs"), supervisor, launcher: new DeterministicLauncher(temp.Path));
        runner.Start(request, temp.Path);

        var immediately = runner.GetVerifiedIdentity(request.TaskId);
        Assert.NotNull(immediately);
        await WaitUntilAsync(() => File.Exists(Path.Combine(temp.Path, "logs", request.TaskId + ".log")) &&
            File.ReadAllText(Path.Combine(temp.Path, "logs", request.TaskId + ".log")).Contains("[redacted]", StringComparison.Ordinal));
        Assert.Contains("stdout-line", File.ReadAllText(Path.Combine(temp.Path, "logs", request.TaskId + ".log")));
        Assert.Contains("[redacted]", File.ReadAllText(Path.Combine(temp.Path, "logs", request.TaskId + ".log")));
        await WaitUntilAsync(() => File.Exists(Path.Combine(temp.Path, "logs", request.TaskId + ".exitcode")));
        supervisor.Alive = false;
        var recoveredRunner = new ProcessRunner(new TaskStore(Path.Combine(temp.Path, "state")), Path.Combine(temp.Path, "logs"), supervisor);
        Assert.Equal(AgentTaskStatus.Succeeded, recoveredRunner.Reconcile(request.TaskId).Status);
        Assert.Equal(AgentTaskStatus.Succeeded, store.FindByTaskId(request.TaskId)!.Status.Status);
    }

    [Fact]
    public void Cancellation_uses_persisted_identity_and_only_target_task()
    {
        using var temp = new TemporaryDirectory();
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var first = Request("task-first");
        var second = Request("task-second");
        store.Submit(first); store.Submit(second);
        store.WriteStatus(Running(first)); store.WriteStatus(Running(second));
        var supervisor = new FakeSupervisor();
        var runner = new ProcessRunner(store, Path.Combine(temp.Path, "logs"), supervisor);
        Directory.CreateDirectory(Path.Combine(temp.Path, "logs"));
        store.WriteProcessIdentity(first.TaskId, new ProcessIdentity(110, 11));
        store.WriteProcessIdentity(second.TaskId, new ProcessIdentity(220, 22));

        Assert.True(runner.Cancel(first.TaskId));
        Assert.Equal([new ProcessIdentity(110, 11)], supervisor.Terminated);
        Assert.Equal(AgentTaskStatus.Cancelled, store.FindByTaskId(first.TaskId)!.Status.Status);
        Assert.Equal(AgentTaskStatus.Running, store.FindByTaskId(second.TaskId)!.Status.Status);
    }

    [Fact]
    public void Identity_validation_rejects_pid_reuse()
    {
        using var temp = new TemporaryDirectory();
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var request = Request("task-identity");
        store.Submit(request); store.WriteStatus(Running(request));
        Directory.CreateDirectory(Path.Combine(temp.Path, "logs"));
        WriteIdentity(temp.Path, request.TaskId, new ProcessIdentity(123, 1));
        var supervisor = new FakeSupervisor { Alive = false };
        var runner = new ProcessRunner(store, Path.Combine(temp.Path, "logs"), supervisor);

        Assert.Null(runner.GetVerifiedIdentity(request.TaskId));
        Assert.False(runner.Cancel(request.TaskId));
        Assert.Empty(supervisor.Terminated);
    }

    [Fact]
    public void System_supervisor_checks_pid_and_exact_process_start_time()
    {
        using var process = Process.GetCurrentProcess();
        var supervisor = new SystemProcessSupervisor();
        var identity = new ProcessIdentity(process.Id, process.StartTime.ToUniversalTime().Ticks);

        Assert.True(supervisor.IsAlive(identity));
        Assert.False(supervisor.IsAlive(identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + TimeSpan.TicksPerSecond }));
    }

    [Fact]
    public void Fresh_agent_reconciles_exited_task_from_persisted_identity_and_exit_code()
    {
        using var temp = new TemporaryDirectory();
        var stateDir = Path.Combine(temp.Path, "state");
        var logs = Path.Combine(temp.Path, "logs");
        var store = new TaskStore(stateDir);
        var request = Request("task-recovered");
        store.Submit(request);
        store.WriteStatus(Running(request));
        Directory.CreateDirectory(logs);
        store.WriteProcessIdentity(request.TaskId, new ProcessIdentity(321, 777));
        File.WriteAllText(Path.Combine(logs, request.TaskId + ".exitcode"), "0");
        var runner = new ProcessRunner(new TaskStore(stateDir), logs, new FakeSupervisor { Alive = false });

        var status = runner.Reconcile(request.TaskId);
        var report = new TaskStore(stateDir).FindByTaskId(request.TaskId)!.Report;

        Assert.Equal(AgentTaskStatus.Succeeded, status.Status);
        Assert.NotNull(report);
        Assert.Equal(request.TaskId, report!.TaskId);
    }

    private static TaskRequest Request(string id) => new(TaskContract.SchemaVersion, id, "key-" + id,
        "https://github.com/acme/repo/issues/1", AgentRelayRole.Worker, "device-a", Path.GetTempPath(), Path.Combine(Path.GetTempPath(), id), "task/" + id, 1);
    private static TaskStatusRecord Running(TaskRequest request) => new(TaskContract.SchemaVersion, request.TaskId,
        request.IdempotencyKey, request.DeviceId, AgentTaskStatus.Running, DateTimeOffset.UtcNow);
    private static void WriteIdentity(string root, string id, ProcessIdentity identity) => File.WriteAllText(Path.Combine(root, "logs", id + ".process.json"), JsonSerializer.Serialize(identity));
    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Child process did not finish in time.");
            await Task.Delay(50);
        }
    }
    private sealed class FakeSupervisor : IProcessSupervisor
    {
        public bool Alive { get; set; } = true;
        public List<ProcessIdentity> Terminated { get; } = [];
        public bool IsAlive(ProcessIdentity identity) => Alive;
        public void Terminate(ProcessIdentity identity) => Terminated.Add(identity);
        public int? ReadExitCode(ProcessIdentity identity, string exitCodePath) => File.Exists(exitCodePath) && int.TryParse(File.ReadAllText(exitCodePath), out var code) ? code : null;
    }
    private sealed class DeterministicLauncher(string directory) : IChildProcessLauncher
    {
        public ChildProcess Start(ProcessStartInfo ignored)
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") : "/bin/sh")
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            if (OperatingSystem.IsWindows())
            {
                start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c");
                start.ArgumentList.Add("echo stdout-line & echo token=topsecret 1>&2 & timeout /t 2 /nobreak >nul & exit /b 0");
            }
            else
            {
                start.ArgumentList.Add("-c"); start.ArgumentList.Add("echo stdout-line; echo token=topsecret >&2; sleep 2; exit 0");
            }
            var process = Process.Start(start)!;
            return new ChildProcess(process, process.StandardInput);
        }
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true); }
    }
}
