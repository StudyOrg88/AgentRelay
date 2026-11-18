using AgentRelay.Gate;

namespace AgentRelay.Gate.Tests;

public sealed class ProcessInvokerTests
{
    [Fact]
    public async Task ArgumentsArePassedWithoutShellExpansion()
    {
        var invoker = new ProcessInvoker();
        var result = await invoker.InvokeAsync("dotnet", ["--version"], new Dictionary<string, string?>(), CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(result.StandardOutput));
    }
}
