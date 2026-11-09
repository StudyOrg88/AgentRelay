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
            File.ReadAllText(Path.Combine(temp.Path, "logs", request.TaskId + ".log")).Contains("[redacted]", StringComparison.Ordinal) &&
            File.ReadAllText(Path.Combine(temp.Path, "logs", request.TaskId + ".log")).Contains("stdout-line", StringComparison.Ordinal));
        Assert.Contains("stdout-line", File.ReadAllText(Path.Combine(temp.Path, "logs", request.TaskId + ".log")));
        Assert.Contains("[redacted]", File.ReadAllText(Path.Combine(temp.Path, "logs", request.TaskId + ".log")));
        var persistedLog = File.ReadAllText(Path.Combine(temp.Path, "logs", request.TaskId + ".log"));
        Assert.DoesNotContain("topsecret", persistedLog);
        Assert.DoesNotContain("jsonsecret", persistedLog);
        Assert.DoesNotContain("bearersecret", persistedLog);
        Assert.DoesNotContain("querysecret", persistedLog);
        await WaitUntilAsync(() => File.Exists(Path.Combine(temp.Path, "logs", request.TaskId + ".exitcode")));
        Assert.False(new SystemProcessSupervisor().IsAlive(store.FindByTaskId(request.TaskId)!.ProcessIdentity!));
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
    public void Codex_executable_name_is_resolved_to_absolute_path_for_supervisor()
    {
        using var temp = new TemporaryDirectory();
        var bin = Path.Combine(temp.Path, "bin"); Directory.CreateDirectory(bin);
        var name = OperatingSystem.IsWindows() ? "codex.exe" : "codex";
        File.WriteAllText(Path.Combine(bin, name), "fake executable");
        var executable = ProcessRunner.ResolveExecutable("codex", temp.Path, bin);

        Assert.True(Path.IsPathFullyQualified(executable));
        Assert.EndsWith(name, executable, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Task_input_is_redacted_and_removed_after_process_finishes()
    {
        using var temp = new TemporaryDirectory();
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var request = Request("task-secret") with { IdempotencyKey = "Bearer supersecret" };
        store.Submit(request);
        var runner = new ProcessRunner(store, Path.Combine(temp.Path, "logs"), launcher: new DeterministicLauncher(temp.Path));

        runner.Start(request, temp.Path);

        var inputPath = Path.Combine(temp.Path, "logs", request.TaskId + ".input.json");
        Assert.False(File.Exists(inputPath));
        await WaitUntilAsync(() => File.Exists(Path.Combine(temp.Path, "logs", request.TaskId + ".exitcode")));
        Assert.Contains("[redacted]", File.ReadAllText(Path.Combine(temp.Path, "logs", request.TaskId + ".log")));
    }

    [Fact]
    public async Task Unix_production_supervisor_tracks_codex_pid_and_task_bound_exit_marker()
    {
        if (OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var fakeCodex = Path.Combine(temp.Path, "codex");
        File.WriteAllText(fakeCodex, "#!/bin/sh\necho wrapper-test-output\nsleep 1\nexit 7\n");
        using (var chmod = Process.Start(new ProcessStartInfo("chmod") { UseShellExecute = false, ArgumentList = { "+x", fakeCodex } })!) chmod.WaitForExit();
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var request = Request("task-unix-production"); store.Submit(request);
        var runner = new ProcessRunner(store, Path.Combine(temp.Path, "logs"), codexExecutable: fakeCodex);

        var identity = runner.Start(request, temp.Path);
        var persisted = store.FindByTaskId(request.TaskId)!.ProcessIdentity!;
        Assert.Equal(identity, persisted);
        Assert.True(new SystemProcessSupervisor().IsAlive(identity));
        await WaitUntilAsync(() => File.Exists(Path.Combine(temp.Path, "logs", request.TaskId + ".exitcode")));
        await WaitUntilAsync(() => !new SystemProcessSupervisor().IsAlive(identity));
        Assert.Equal(AgentTaskStatus.Failed, runner.Reconcile(request.TaskId).Status);
        var launchToken = store.FindByTaskId(request.TaskId)!.LaunchToken!;
        Assert.Equal($"{request.TaskId}:{launchToken}:{identity.ProcessId}:{identity.StartTimeUtcTicks}:7", File.ReadAllText(Path.Combine(temp.Path, "logs", request.TaskId + ".exitcode")));
    }

    [Theory]
    [InlineData(0, AgentTaskStatus.Succeeded)]
    [InlineData(7, AgentTaskStatus.Failed)]
    public async Task Windows_production_supervisor_preserves_child_exit_code(int childExitCode, AgentTaskStatus expectedStatus)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var bin = Path.Combine(temp.Path, "bin"); Directory.CreateDirectory(bin);
        var fakeCodex = Path.Combine(bin, "codex.exe");
        var taskId = "task-windows-exit-" + childExitCode;
        var signal = Path.Combine(temp.Path, "signal");
        var fakeCodexSource = Path.Combine(temp.Path, "Program.cs");
        var fakeCodexProject = Path.Combine(temp.Path, "FakeCodex.csproj");
        File.WriteAllText(fakeCodexSource, $"using System;\nusing System.IO;\nusing System.Threading;\ninternal static class Program {{\n    private static int Main() {{\n        File.WriteAllText(Environment.GetEnvironmentVariable(\"AGENTRELAY_TEST_SIGNAL\")!, \"ready\");\n        Thread.Sleep(2000);\n        Console.WriteLine(\"fake stdout\");\n        Console.Error.WriteLine(\"fake stderr\");\n        return {childExitCode};\n    }}\n}}\n");
        File.WriteAllText(fakeCodexProject, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><PublishAot>true</PublishAot><SelfContained>true</SelfContained></PropertyGroup></Project>");
        var dotnetHost = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        var build = Process.Start(new ProcessStartInfo(dotnetHost)
        {
            WorkingDirectory = temp.Path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "publish", fakeCodexProject, "--configuration", "Release", "--runtime", "win-x64", "--self-contained", "true", "-p:UseAppHost=true", "--output", bin, "--verbosity", "quiet" }
        })!;
        var buildOutput = build.StandardOutput.ReadToEnd() + build.StandardError.ReadToEnd();
        build.WaitForExit();
        Assert.True(build.ExitCode == 0, buildOutput);
        File.Move(Path.Combine(bin, "FakeCodex.exe"), fakeCodex);
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var request = Request(taskId); store.Submit(request);
        var logs = Path.Combine(temp.Path, "logs");
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        var oldSignal = Environment.GetEnvironmentVariable("AGENTRELAY_TEST_SIGNAL");
        Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + oldPath);
        Environment.SetEnvironmentVariable("AGENTRELAY_TEST_SIGNAL", signal);
        var runner = new ProcessRunner(store, logs, codexExecutable: "codex");
        try
        {
            var identity = runner.Start(request, temp.Path);
            await WaitUntilAsync(() => File.Exists(signal));
            var task = store.FindByTaskId(request.TaskId)!;
            Assert.Equal(identity, task.ProcessIdentity);
            var phase = Path.Combine(logs, request.TaskId + ".launch-phase");
            var exitMarker = Path.Combine(logs, request.TaskId + ".exitcode");
            await WaitUntilAsync(() => File.Exists(exitMarker) ||
                (File.Exists(phase) && File.ReadAllText(phase).EndsWith(":error", StringComparison.Ordinal)));
            Assert.True(!File.Exists(phase) || !File.ReadAllText(phase).EndsWith(":error", StringComparison.Ordinal),
                File.Exists(Path.Combine(logs, request.TaskId + ".launch.wrapper.error"))
                    ? File.ReadAllText(Path.Combine(logs, request.TaskId + ".launch.wrapper.error"))
                    : "wrapper error was not persisted");
            await WaitUntilAsync(() => File.ReadAllText(phase).EndsWith(":completed", StringComparison.Ordinal));
            Assert.EndsWith(":completed", File.ReadAllText(phase));
            var marker = File.ReadAllText(exitMarker);
            Assert.True(new SystemProcessSupervisor().ReadExitCode(identity,
                exitMarker, request.TaskId, task.LaunchToken!) == childExitCode, marker);
            Assert.Null(new SystemProcessSupervisor().ReadExitCode(identity with { StartTimeUtcTicks = identity.StartTimeUtcTicks + 1 },
                Path.Combine(logs, request.TaskId + ".exitcode"), request.TaskId, task.LaunchToken!));
            Assert.Null(new SystemProcessSupervisor().ReadExitCode(identity, Path.Combine(logs, request.TaskId + ".exitcode"),
                request.TaskId, task.LaunchToken + "wrong"));
            await WaitUntilAsync(() => runner.Reconcile(request.TaskId).Status == expectedStatus);
            Assert.Equal(expectedStatus, store.FindByTaskId(request.TaskId)!.Status.Status);
            Assert.Equal(expectedStatus, store.FindByTaskId(request.TaskId)!.Report!.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", oldPath);
            Environment.SetEnvironmentVariable("AGENTRELAY_TEST_SIGNAL", oldSignal);
        }
    }

    [Fact]
    public void Windows_partial_launch_phase_without_identity_is_diagnosable_and_does_not_relaunch()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var temp = new TemporaryDirectory();
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var request = Request("task-partial-wrapper"); store.Submit(request);
        store.WriteStatus(Running(request) with { Status = AgentTaskStatus.Starting });
        var token = store.WriteLaunchToken(request.TaskId);
        var logs = Path.Combine(temp.Path, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, request.TaskId + ".launch-phase"), $"{request.TaskId}:{token}:starting");
        var launcher = new CountingLauncher();
        var runner = new ProcessRunner(store, logs, launcher: launcher);

        var exception = Assert.Throws<InvalidOperationException>(() => runner.Start(request, temp.Path));

        Assert.Contains("phase 'starting'", exception.Message);
        Assert.Equal(0, launcher.StartCount);
    }

    [Fact]
    public void Exit_marker_rejects_pid_reuse_and_task_or_token_mismatch()
    {
        using var temp = new TemporaryDirectory();
        var identity = new ProcessIdentity(444, 555);
        var path = Path.Combine(temp.Path, "task.exitcode");
        var supervisor = new SystemProcessSupervisor();
        File.WriteAllText(path, "task-a:token-a:444:556:0");

        Assert.Null(supervisor.ReadExitCode(identity, path, "task-a", "token-a"));
        File.WriteAllText(path, "task-b:token-a:444:555:0");
        Assert.Null(supervisor.ReadExitCode(identity, path, "task-a", "token-a"));
        File.WriteAllText(path, "task-a:token-b:444:555:0");
        Assert.Null(supervisor.ReadExitCode(identity, path, "task-a", "token-a"));
        File.WriteAllText(path, "task-a:token-a:444:555:0");
        Assert.Equal(0, supervisor.ReadExitCode(identity, path, "task-a", "token-a"));
    }

    [Fact]
    public void Reconcile_rejects_exit_marker_when_store_identity_differs_from_launch_sidecar()
    {
        using var temp = new TemporaryDirectory();
        var store = new TaskStore(Path.Combine(temp.Path, "state"));
        var request = Request("task-identity-mismatch"); store.Submit(request);
        store.WriteStatus(Running(request));
        var token = store.WriteLaunchToken(request.TaskId);
        store.WriteProcessIdentity(request.TaskId, new ProcessIdentity(444, 555));
        var logs = Path.Combine(temp.Path, "logs"); Directory.CreateDirectory(logs);
        File.WriteAllText(Path.Combine(logs, request.TaskId + ".launch"), $"{request.TaskId}:{token}:444:556");
        File.WriteAllText(Path.Combine(logs, request.TaskId + ".exitcode"), $"{request.TaskId}:{token}:444:555:0");
        var runner = new ProcessRunner(store, logs, new FakeSupervisor { Alive = false });

        var result = runner.Reconcile(request.TaskId);

        Assert.Equal(AgentTaskStatus.Running, result.Status);
        Assert.Null(store.FindByTaskId(request.TaskId)!.Report);
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
        var launchToken = store.WriteLaunchToken(request.TaskId);
        store.WriteProcessIdentity(request.TaskId, new ProcessIdentity(321, 777));
        File.WriteAllText(Path.Combine(logs, request.TaskId + ".launch"), $"{request.TaskId}:{launchToken}:321:777");
        File.WriteAllText(Path.Combine(logs, request.TaskId + ".exitcode"), $"{request.TaskId}:{launchToken}:321:777:0");
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
        public int? ReadExitCode(ProcessIdentity identity, string exitCodePath, string taskId, string launchToken)
        {
            if (!File.Exists(exitCodePath)) return null;
            var fields = File.ReadAllText(exitCodePath).Split(':');
            return fields.Length == 5 && fields[0] == taskId && fields[1] == launchToken &&
                int.TryParse(fields[2], out var pid) && long.TryParse(fields[3], out var ticks) &&
                int.TryParse(fields[4], out var code) && pid == identity.ProcessId && ticks == identity.StartTimeUtcTicks ? code : null;
        }
    }
    private sealed class DeterministicLauncher(string directory) : IChildProcessLauncher
    {
        public ChildProcess Start(ProcessStartInfo ignored)
        {
            var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? "powershell.exe" : "/bin/sh")
            { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            if (OperatingSystem.IsWindows())
            {
                start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-Command");
                start.ArgumentList.Add("Write-Output 'stdout-line'; [Console]::Error.WriteLine('token=topsecret'); Write-Output '{\"token\":\"jsonsecret\"}'; Write-Output 'Bearer bearersecret'; Write-Output 'https://host/?access_token=querysecret'; Start-Sleep -Seconds 2; exit 0");
            }
            else
            {
                start.ArgumentList.Add("-c"); start.ArgumentList.Add("echo stdout-line; echo token=topsecret >&2; echo '{\"token\":\"jsonsecret\"}'; echo 'Bearer bearersecret'; echo 'https://host/?access_token=querysecret'; sleep 2; exit 0");
            }
            var process = Process.Start(start)!;
            return new ChildProcess(process, process.StandardInput);
        }
    }
    private sealed class CountingLauncher : IChildProcessLauncher
    {
        public int StartCount { get; private set; }
        public ChildProcess Start(ProcessStartInfo startInfo)
        {
            StartCount++;
            throw new InvalidOperationException("Unexpected launcher invocation.");
        }
    }
    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose()
        {
            if (!Directory.Exists(Path)) return;
            var deadline = DateTime.UtcNow.AddSeconds(3);
            while (true)
            {
                try { Directory.Delete(Path, recursive: true); return; }
                catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
                catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline) { Thread.Sleep(25); }
            }
        }
    }
}
