using System.Text.Json;
using AgentRelay.Agent;
using AgentRelay.Core;
using Xunit;
using AgentTaskStatus = AgentRelay.Core.TaskStatus;

namespace AgentRelay.Agent.Tests;

public sealed class AgentCliTests
{
    [Fact]
    public async Task Preflight_and_status_use_json_responses_and_status_is_task_scoped()
    {
        using var temp = new TemporaryDirectory();
        var repo = temp.CreateGitRepository();
        var device = new RegisteredDevice("device-a", "Local", OperatingSystem.IsWindows() ? "windows" : "ubuntu", "local", null, null, null,
            [repo], ["worker", "reviewer"], true, new Dictionary<string, int> { ["worker"] = 1, ["reviewer"] = 1 });
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var runner = new ProcessRunner(store, Path.Combine(temp.Path, "logs"), new NeverAliveSupervisor());
        var service = new AgentService(device, store, new WorktreeManager([repo]), runner);

        using var preflight = JsonDocument.Parse(await service.ExecuteAsync("preflight", "{}"));
        Assert.True(preflight.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal("device-a", preflight.RootElement.GetProperty("device_id").GetString());
        await Assert.ThrowsAsync<JsonException>(() => service.ExecuteAsync("status", "{}"));
        await Assert.ThrowsAsync<JsonException>(() => service.ExecuteAsync("unknown", "{}"));
    }

    [Fact]
    public async Task Program_reads_json_stdin_and_writes_only_json_stdout()
    {
        using var temp = new TemporaryDirectory();
        var repo = temp.CreateGitRepository();
        var registryPath = Path.Combine(temp.Path, "devices.txt");
        File.WriteAllText(registryPath, JsonSerializer.Serialize(new
        {
            schema_version = DeviceRegistry.SchemaVersion,
            devices = new[] { new { id = "device-a", name = "Local", os = OperatingSystem.IsWindows() ? "windows" : "ubuntu",
                connection = "local", host = (string?)null, port = (int?)null, user = (string?)null,
                repositories = new[] { repo }, roles = new[] { "worker" }, enabled = true } }
        }));
        var configPath = Path.Combine(temp.Path, "agent.json");
        File.WriteAllText(configPath, JsonSerializer.Serialize(new { registry = registryPath, repository_root = temp.Path,
            device_id = "device-a", state_directory = Path.Combine(temp.Path, "state"), log_directory = Path.Combine(temp.Path, "logs") }));
        var oldConfig = Environment.GetEnvironmentVariable("AGENTRELAY_CONFIG");
        var oldIn = Console.In; var oldOut = Console.Out; var oldError = Console.Error;
        using var output = new StringWriter(); using var errors = new StringWriter();
        try
        {
            Environment.SetEnvironmentVariable("AGENTRELAY_CONFIG", configPath);
            Console.SetIn(new StringReader("{}")); Console.SetOut(output); Console.SetError(errors);
            var exit = await Program.Main(["preflight"]);
            Assert.Equal(0, exit);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTRELAY_CONFIG", oldConfig);
            Console.SetIn(oldIn); Console.SetOut(oldOut); Console.SetError(oldError);
        }
        using var response = JsonDocument.Parse(output.ToString());
        Assert.True(response.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(string.Empty, errors.ToString());
    }

    [Fact]
    public async Task Submit_rejects_unregistered_repository_and_role_before_acceptance()
    {
        using var temp = new TemporaryDirectory();
        var repo = temp.CreateGitRepository();
        var outside = temp.CreateGitRepository("outside");
        var device = new RegisteredDevice("device-a", "Local", "ubuntu", "local", null, null, null,
            [repo], ["worker"], true, new Dictionary<string, int> { ["worker"] = 1 });
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var service = new AgentService(device, store, new WorktreeManager([repo]), new ProcessRunner(store, Path.Combine(temp.Path, "logs")));
        var request = Request(outside);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync("submit", request.ToJson()));
        Assert.Null(store.FindByTaskId(request.TaskId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExecuteAsync("submit", (request with { Repository = repo, Role = AgentRelayRole.Reviewer }).ToJson()));
        Assert.Null(store.FindByTaskId(request.TaskId));
    }

    [Fact]
    public async Task Concurrent_same_key_submissions_create_and_start_exactly_one_task()
    {
        using var temp = new TemporaryDirectory();
        var repo = temp.CreateGitRepository();
        var request = Request(repo);
        var device = new RegisteredDevice("device-a", "Local", OperatingSystem.IsWindows() ? "windows" : "ubuntu", "local", null, null, null,
            [repo], ["worker"], true, new Dictionary<string, int> { ["worker"] = 1 });
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var launcher = new CountingLauncher();
        var runner = new ProcessRunner(store, Path.Combine(temp.Path, "logs"), launcher: launcher);
        var service = new AgentService(device, store, new WorktreeManager([repo]), runner);

        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => service.ExecuteAsync("submit", request.ToJson())));

        Assert.Equal(1, launcher.StartCount);
        Assert.Equal(16, results.Length);
        Assert.All(results, result => Assert.True(new[] { "running", "failed" }.Contains(JsonDocument.Parse(result).RootElement.GetProperty("status").GetString())));
        Assert.NotNull(store.FindByTaskId(request.TaskId));
    }

    [Fact]
    public async Task Failed_launch_leaves_retryable_starting_task_and_retry_starts_once()
    {
        using var temp = new TemporaryDirectory();
        var repo = temp.CreateGitRepository();
        var request = Request(repo);
        var device = new RegisteredDevice("device-a", "Local", OperatingSystem.IsWindows() ? "windows" : "ubuntu", "local", null, null, null,
            [repo], ["worker"], true, new Dictionary<string, int> { ["worker"] = 1 });
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var launcher = new CountingLauncher();
        var runner = new ProcessRunner(store, Path.Combine(temp.Path, "logs"), launcher: launcher);
        var failOnce = true;
        store.BeforeWriteProcessIdentity = () =>
        {
            if (!failOnce) return;
            failOnce = false;
            throw new IOException("injected WriteProcessIdentity failure");
        };
        var service = new AgentService(device, store, new WorktreeManager([repo]), runner);

        await Assert.ThrowsAsync<IOException>(() => service.ExecuteAsync("submit", request.ToJson()));
        Assert.Equal(AgentTaskStatus.Starting, store.FindByTaskId(request.TaskId)!.Status.Status);
        Assert.Null(store.FindByTaskId(request.TaskId)!.ProcessIdentity);

        using var response = JsonDocument.Parse(await service.ExecuteAsync("submit", request.ToJson()));
        Assert.Equal("running", response.RootElement.GetProperty("status").GetString());
        Assert.Equal(1, launcher.StartCount);
    }

    [Fact]
    public async Task Existing_idempotency_key_returns_original_task_if_request_workspace_was_removed()
    {
        using var temp = new TemporaryDirectory();
        var repo = temp.CreateGitRepository();
        var request = Request(repo);
        var device = new RegisteredDevice("device-a", "Local", OperatingSystem.IsWindows() ? "windows" : "ubuntu", "local", null, null, null,
            [repo], ["worker"], true, new Dictionary<string, int> { ["worker"] = 1 });
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        store.Submit(request);
        store.WriteStatus(new TaskStatusRecord(TaskContract.SchemaVersion, request.TaskId, request.IdempotencyKey,
            request.DeviceId, AgentTaskStatus.Failed, DateTimeOffset.UtcNow));
        store.WriteReport(new TaskReport(TaskContract.SchemaVersion, request.TaskId, request.IdempotencyKey,
            request.IssueUrl, request.Role, request.DeviceId, AgentTaskStatus.Failed, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, request.Branch, null, [], [], ["original"], null));
        var service = new AgentService(device, store, new WorktreeManager([repo]),
            new ProcessRunner(store, Path.Combine(temp.Path, "logs"), new NeverAliveSupervisor()));

        using var response = JsonDocument.Parse(await service.ExecuteAsync("submit", request.ToJson()));

        Assert.Equal("failed", response.RootElement.GetProperty("status").GetString());
        Assert.Equal(AgentTaskStatus.Failed, store.FindByTaskId(request.TaskId)!.Status.Status);
    }

    [Fact]
    public async Task Status_report_and_cancel_reconcile_by_task_id_and_preserve_workspace()
    {
        using var temp = new TemporaryDirectory();
        var repo = temp.CreateGitRepository();
        var workspace = Path.Combine(repo, ".agentrelay-worktrees", "cli-task");
        Directory.CreateDirectory(workspace);
        var device = new RegisteredDevice("device-a", "Local", OperatingSystem.IsWindows() ? "windows" : "ubuntu", "local", null, null, null,
            [repo], ["worker"], true, new Dictionary<string, int> { ["worker"] = 1 });
        var statePath = Path.Combine(temp.Path, "state");
        var logs = Path.Combine(temp.Path, "logs");
        Directory.CreateDirectory(logs);
        var request = Request(repo);
        var store = new TaskStore(statePath);
        store.Submit(request);
        store.WriteStatus(new TaskStatusRecord(TaskContract.SchemaVersion, request.TaskId, request.IdempotencyKey,
            request.DeviceId, AgentTaskStatus.Running, DateTimeOffset.UtcNow));
        var identity = new ProcessIdentity(515, 1234);
        var launchToken = store.WriteLaunchToken(request.TaskId);
        store.WriteProcessIdentity(request.TaskId, identity);
        File.WriteAllText(Path.Combine(logs, request.TaskId + ".exitcode"), $"{request.TaskId}:{launchToken}:{identity.ProcessId}:{identity.StartTimeUtcTicks}:0");
        var supervisor = new FakeSupervisor { Alive = false };
        var service = new AgentService(device, new TaskStore(statePath), new WorktreeManager([repo]), new ProcessRunner(new TaskStore(statePath), logs, supervisor));

        using var status = JsonDocument.Parse(await service.ExecuteAsync("status", "{\"task_id\":\"cli-task\"}"));
        Assert.Equal("succeeded", status.RootElement.GetProperty("status").GetProperty("status").GetString());
        using var report = JsonDocument.Parse(await service.ExecuteAsync("report", "{\"task_id\":\"cli-task\"}"));
        Assert.Equal("cli-task", report.RootElement.GetProperty("task_id").GetString());
        using var cancelled = JsonDocument.Parse(await service.ExecuteAsync("cancel", "{\"task_id\":\"cli-task\"}"));
        Assert.False(cancelled.RootElement.GetProperty("cancelled").GetBoolean());
        Assert.True(Directory.Exists(workspace));
    }

    private static TaskRequest Request(string repository) => new(TaskContract.SchemaVersion, "cli-task", "cli-key",
        "https://github.com/acme/repo/issues/8", AgentRelayRole.Worker, "device-a", repository,
        Path.Combine(repository, ".agentrelay-worktrees", "cli-task"), "task/cli", 1);

    private sealed class NeverAliveSupervisor : IProcessSupervisor
    {
        public bool IsAlive(ProcessIdentity identity) => false;
        public void Terminate(ProcessIdentity identity) => throw new InvalidOperationException();
        public int? ReadExitCode(ProcessIdentity identity, string exitCodePath, string taskId, string launchToken) => null;
    }
    private sealed class CountingLauncher : IChildProcessLauncher
    {
        public int StartCount { get; private set; }
        public ChildProcess Start(System.Diagnostics.ProcessStartInfo ignored)
        {
            StartCount++;
            var start = new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsWindows() ? (Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe") : "/bin/sh")
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (OperatingSystem.IsWindows()) { start.ArgumentList.Add("/d"); start.ArgumentList.Add("/c"); start.ArgumentList.Add("timeout /t 3 /nobreak >nul"); }
            else { start.ArgumentList.Add("-c"); start.ArgumentList.Add("sleep 3"); }
            var process = System.Diagnostics.Process.Start(start)!;
            return new ChildProcess(process, process.StandardInput);
        }
    }
    private sealed class FakeSupervisor : IProcessSupervisor
    {
        public bool Alive { get; set; } = true;
        public bool IsAlive(ProcessIdentity identity) => Alive;
        public void Terminate(ProcessIdentity identity) { }
        public int? ReadExitCode(ProcessIdentity identity, string path, string taskId, string launchToken)
        {
            if (!File.Exists(path)) return null;
            var fields = File.ReadAllText(path).Split(':');
            return fields.Length == 5 && fields[0] == taskId && fields[1] == launchToken &&
                int.TryParse(fields[2], out var pid) && long.TryParse(fields[3], out var ticks) &&
                int.TryParse(fields[4], out var code) && pid == identity.ProcessId && ticks == identity.StartTimeUtcTicks ? code : null;
        }
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public string CreateGitRepository(string name = "repo")
        {
            var path = System.IO.Path.Combine(Path, name); Directory.CreateDirectory(path);
            RunGit(path, "init", "--quiet");
            RunGit(path, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "--allow-empty", "-m", "initial", "--quiet");
            return path;
        }
        private static void RunGit(string working, params string[] args)
        {
            var start = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = working, UseShellExecute = false };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var p = System.Diagnostics.Process.Start(start)!; p.WaitForExit(); Assert.Equal(0, p.ExitCode);
        }
        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            foreach (var entry in Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories))
                try { File.SetAttributes(entry, FileAttributes.Normal); } catch { }
            Directory.Delete(Path, true);
        }
    }
}
