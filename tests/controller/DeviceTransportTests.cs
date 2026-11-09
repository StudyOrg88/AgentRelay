using AgentRelay.Controller;
using AgentRelay.Core;
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

    private static RegisteredDevice Device(string connection) => new("dev1", "Build host", "ubuntu", connection,
        connection == "ssh" ? "build-host" : null, connection == "ssh" ? 2222 : null,
        connection == "ssh" ? "chen" : null, ["/workspace"], ["worker"], true,
        new Dictionary<string, int> { ["worker"] = 1 });

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
