using AgentRelay.Controller;
using AgentRelay.Core;
using System.Diagnostics;
using Xunit;

namespace AgentRelay.Controller.Tests;

public sealed class DeviceTransportTests
{
    [Fact]
    public async Task SshUsesRegisteredConnectionAndFixedCommandWhilePayloadStaysOnStdin()
    {
        var fake = new FakeInvoker { Result = new(0, "{\"ok\":true}", "") };
        var payload = "{\"prompt\":\"x'; touch /tmp/pwned\\nsecret=do-not-log\"}";
        var transport = new DeviceTransport("agentrelay-agent", "ssh", processInvoker: fake);

        var response = await transport.InvokeAsync(Device("ssh"), "submit", payload);

        Assert.Equal("true", response.GetProperty("ok").GetRawText());
        Assert.Equal(payload, fake.Invocation!.StandardInput);
        Assert.Equal("ssh", fake.Invocation.FileName);
        Assert.Contains("StrictHostKeyChecking=yes", fake.Invocation.Arguments);
        Assert.Contains("chen@build-host", fake.Invocation.Arguments);
        Assert.Equal("agentrelay-agent submit", fake.Invocation.Arguments[^1]);
        Assert.DoesNotContain(fake.Invocation.Arguments, argument => argument.Contains("pwned") || argument.Contains("secret"));
    }

    [Fact]
    public async Task LocalDeviceInvokesAgentDirectly()
    {
        var calls = 0;
        var transport = new DeviceTransport(localInvoker: (_, operation, payload, _) =>
        {
            calls++;
            Assert.Equal("preflight", operation);
            Assert.Equal("{}", payload);
            return Task.FromResult("{\"ok\":true}");
        });

        await transport.InvokeAsync(Device("local"), "preflight", "{}");
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("host key verification failed", "host identity is not trusted")]
    [InlineData("private-key-material", "non-zero exit code")]
    public async Task NonZeroExitFailsWithSanitizedDiagnostic(string stderr, string expected)
    {
        var fake = new FakeInvoker { Result = new(255, "{}", stderr) };
        var exception = await Assert.ThrowsAsync<DeviceTransportException>(() =>
            new DeviceTransport(processInvoker: fake).InvokeAsync(Device("ssh"), "status", "{}"));
        Assert.Contains(expected, exception.Message);
        Assert.DoesNotContain("private-key-material", exception.Message);
    }

    [Fact]
    public async Task TimeoutFailsClosed()
    {
        var fake = new FakeInvoker { WaitForCancellation = true };
        var exception = await Assert.ThrowsAsync<DeviceTransportException>(() =>
            new DeviceTransport(timeout: TimeSpan.FromMilliseconds(20), processInvoker: fake)
                .InvokeAsync(Device("ssh"), "status", "{}"));
        Assert.Contains("timed out", exception.Message);
    }

    [Fact]
    public async Task NonJsonOutputFailsWithoutEchoingOutput()
    {
        var fake = new FakeInvoker { Result = new(0, "token=top-secret", "") };
        var exception = await Assert.ThrowsAsync<DeviceTransportException>(() =>
            new DeviceTransport(processInvoker: fake).InvokeAsync(Device("ssh"), "report", "{}"));
        Assert.Contains("invalid JSON", exception.Message);
        Assert.DoesNotContain("top-secret", exception.Message);
    }

    [Fact]
    public async Task UnknownOperationIsRejectedBeforeProcessLaunch()
    {
        var fake = new FakeInvoker();
        await Assert.ThrowsAsync<ArgumentException>(() =>
            new DeviceTransport(processInvoker: fake).InvokeAsync(Device("ssh"), "run arbitrary", "{}"));
        Assert.Null(fake.Invocation);
    }

    [Theory]
    [InlineData("agent;touch")]
    [InlineData("/opt/agentrelay-agent")]
    [InlineData("agent relay")]
    public void RemoteExecutableMustBeASingleTrustedBasename(string executable)
    {
        Assert.Throws<ArgumentException>(() => new DeviceTransport(agentExecutable: executable));
    }

    [Fact]
    public async Task TransportRejectsUnsafeSshHostAndUserEvenIfDeviceWasConstructedDirectly()
    {
        var fake = new FakeInvoker();
        var transport = new DeviceTransport(processInvoker: fake);
        foreach (var device in new[] { Device("ssh") with { Host = "safe;touch /tmp/pwned" },
                     Device("ssh") with { User = "user$(id)" } })
        {
            await Assert.ThrowsAsync<DeviceTransportException>(() => transport.InvokeAsync(device, "status", "{}"));
        }
        Assert.Null(fake.Invocation);
    }

    [Fact]
    public async Task RealProcessTimeoutKillsAndWaitsForChildProcess()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"agentrelay-child-{Guid.NewGuid():N}.pid");
        try
        {
            var invocation = LongRunningInvocation(pidFile, TimeSpan.FromMilliseconds(700));
            var task = new DeviceTransport.SystemProcessInvoker().InvokeAsync(invocation, CancellationToken.None);
            var pid = await WaitForPidAsync(pidFile);
            await Assert.ThrowsAsync<DeviceTransportException>(() => task);
            Assert.True(await WaitUntilExitedAsync(pid));
        }
        finally { File.Delete(pidFile); }
    }

    [Fact]
    public async Task CallerCancellationIsPropagatedAndNotReportedAsTimeout()
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"agentrelay-child-{Guid.NewGuid():N}.pid");
        try
        {
            using var cancellation = new CancellationTokenSource();
            var task = new DeviceTransport.SystemProcessInvoker().InvokeAsync(LongRunningInvocation(pidFile, TimeSpan.FromSeconds(5)), cancellation.Token);
            var pid = await WaitForPidAsync(pidFile);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.True(await WaitUntilExitedAsync(pid));
        }
        finally { File.Delete(pidFile); }
    }

    [Theory]
    [InlineData("stdout")]
    [InlineData("stderr")]
    public async Task ExcessiveOutputFailsAndTerminatesProcess(string stream)
    {
        var pidFile = Path.Combine(Path.GetTempPath(), $"agentrelay-output-{Guid.NewGuid():N}.pid");
        try
        {
            var invocation = OutputInvocation(stream, pidFile);
            var invoker = new DeviceTransport.SystemProcessInvoker(maximumStandardOutputBytes: 1024, maximumStandardErrorBytes: 1024);
            var task = invoker.InvokeAsync(invocation, CancellationToken.None);
            var pid = await WaitForPidAsync(pidFile);
            var exception = await Assert.ThrowsAsync<DeviceTransportException>(() => task);
            Assert.Contains("output exceeded", exception.Message);
            Assert.Equal(1025, exception.OutputBytesRead);
            Assert.True(await WaitUntilExitedAsync(pid));
        }
        finally { File.Delete(pidFile); }
    }

    [Fact]
    public async Task AcceptsValidJsonAtExactUtf8ByteLimit()
    {
        const string json = "{\"value\":\"ééééé\"}";
        var limit = System.Text.Encoding.UTF8.GetByteCount(json);
        var result = await new DeviceTransport.SystemProcessInvoker(limit, 64)
            .InvokeAsync(OutputTextInvocation(json, "stdout"), CancellationToken.None);

        Assert.Equal(json, result.StandardOutput);
        using var parsed = System.Text.Json.JsonDocument.Parse(result.StandardOutput);
        Assert.Equal("ééééé", parsed.RootElement.GetProperty("value").GetString());
    }

    [Fact]
    public async Task OneByteOverTheExactLimitFailsAsOutputLimit()
    {
        const string json = "{\"value\":\"ééééé\"}";
        var limit = System.Text.Encoding.UTF8.GetByteCount(json);
        var exception = await Assert.ThrowsAsync<DeviceTransportException>(() =>
            new DeviceTransport.SystemProcessInvoker(limit, 64)
                .InvokeAsync(OutputTextInvocation(json + " ", "stdout"), CancellationToken.None));

        Assert.Contains("output exceeded", exception.Message);
        Assert.Equal(limit + 1, exception.OutputBytesRead);
    }

    [Fact]
    public async Task Utf8MultibyteCharacterCrossingByteLimitIsNotCountedAsOneCharacter()
    {
        const string json = "{\"value\":\"ééééé\"}";
        var byteLength = System.Text.Encoding.UTF8.GetByteCount(json);
        var exception = await Assert.ThrowsAsync<DeviceTransportException>(() =>
            new DeviceTransport.SystemProcessInvoker(byteLength - 1, 64)
                .InvokeAsync(OutputTextInvocation(json, "stdout"), CancellationToken.None));

        Assert.Contains("output exceeded", exception.Message);
        Assert.Equal(byteLength, exception.OutputBytesRead);
    }

    private static RegisteredDevice Device(string connection) => new("dev1", "Build host", "ubuntu", connection,
        connection == "ssh" ? "build-host" : null, connection == "ssh" ? 2222 : null,
        connection == "ssh" ? "chen" : null, ["/workspace"], ["worker"], true,
        new Dictionary<string, int> { ["worker"] = 1 });

    private static ProcessInvocation OutputInvocation(string stream, string pidFile)
    {
        var escapedPath = pidFile.Replace("'", "''", StringComparison.Ordinal);
        return OperatingSystem.IsWindows()
            ? new("powershell.exe", ["-NoProfile", "-Command", $"Set-Content -LiteralPath '{escapedPath}' -Value $PID; while ($true) {{ [Console]::{(stream == "stdout" ? "Out" : "Error")}.Write(('x' * 100)); [Console]::{(stream == "stdout" ? "Out" : "Error")}.Flush() }}"], "", TimeSpan.FromSeconds(5))
            : new("/bin/sh", ["-c", $"echo $$ > '{escapedPath}'; exec yes x{(stream == "stderr" ? " >&2" : "")}"], "", TimeSpan.FromSeconds(5));
    }

    private static ProcessInvocation OutputTextInvocation(string output, string stream)
    {
        var escaped = output.Replace("'", "''", StringComparison.Ordinal);
        return OperatingSystem.IsWindows()
            ? new("powershell.exe", ["-NoProfile", "-Command", $"[Console]::OutputEncoding = New-Object System.Text.UTF8Encoding $false; [Console]::{(stream == "stdout" ? "Out" : "Error")}.Write('{escaped}')"], "", TimeSpan.FromSeconds(5))
            : new("/bin/sh", ["-c", $"printf '%s' '{escaped}'{(stream == "stderr" ? " >&2" : "")}"], "", TimeSpan.FromSeconds(5));
    }

    private static ProcessInvocation LongRunningInvocation(string pidFile, TimeSpan timeout)
    {
        var escapedPath = pidFile.Replace("'", "''", StringComparison.Ordinal);
        return OperatingSystem.IsWindows()
            ? new("powershell.exe", ["-NoProfile", "-Command", $"$child = Start-Process powershell.exe -ArgumentList '-NoProfile','-Command','Start-Sleep -Seconds 30' -PassThru; Set-Content -LiteralPath '{escapedPath}' -Value $child.Id; Wait-Process -Id $child.Id"], "", timeout)
            : new("/bin/sh", ["-c", $"sleep 30 & child=$!; echo $child > '{escapedPath}'; wait $child"], "", timeout);
    }

    private static async Task<int> WaitForPidAsync(string path)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (File.Exists(path) && int.TryParse(await File.ReadAllTextAsync(path), out var pid)) return pid;
            }
            catch (IOException) { }
            await Task.Delay(20);
        }
        throw new TimeoutException("The child process did not record its PID.");
    }

    private static async Task<bool> WaitUntilExitedAsync(int pid)
    {
        var until = DateTime.UtcNow + TimeSpan.FromSeconds(3);
        while (DateTime.UtcNow < until)
        {
            try { if (Process.GetProcessById(pid).HasExited) return true; }
            catch (ArgumentException) { return true; }
            await Task.Delay(25);
        }
        return false;
    }

    private sealed class FakeInvoker : IProcessInvoker
    {
        public ProcessInvocation? Invocation { get; private set; }
        public ProcessResult Result { get; init; } = new(0, "{}", "");
        public bool WaitForCancellation { get; init; }
        public async Task<ProcessResult> InvokeAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
        {
            Invocation = invocation;
            if (WaitForCancellation)
                await Task.Delay(invocation.Timeout + TimeSpan.FromSeconds(1), cancellationToken);
            return Result;
        }
    }
}
