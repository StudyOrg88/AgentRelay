using System.Text.Json;
using AgentRelay.Gate;

namespace AgentRelay.Gate.Tests;

public sealed class GateCliTests
{
    [Theory]
    [InlineData("--test-pr", "4", "--expected-head", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("--expected-head", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "--authorization-file", "auth.json")]
    [InlineData("--test-pr", "4", "--authorization-file", "auth.json")]
    public async Task ActiveProbeRequiresAllBoundInputs(params string[] activeArguments)
    {
        var args = new[] { "probe", "--repo", "example/test-repo", "--base", "main", "--expected-check", "build", "--reviewer-app", "reviewer", "--gate-app", "gate", "--merge-app", "merge", "--active", "--test-task", "v2-test" }.Concat(activeArguments).ToArray();
        var output = new StringWriter();
        var exit = await GateCli.RunAsync(args, new GateDependencies(new NoopApi(), TextWriter.Null), output, TextWriter.Null, CancellationToken.None);

        Assert.Equal(2, exit);
        Assert.Contains("active_probe_requires_binding", output.ToString());
    }

    [Fact]
    public async Task ActiveAuthorizationMustMatchRepositoryPrHeadAndNotBeExpired()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new ActiveProbeAuthorization("other/repo", 4, new string('a', 40), DateTimeOffset.UtcNow.AddMinutes(10), true, "v2-test")));
        var output = new StringWriter();
        var args = new[] { "probe", "--repo", "example/test-repo", "--base", "main", "--expected-check", "build", "--reviewer-app", "reviewer", "--gate-app", "gate", "--merge-app", "merge", "--active", "--test-task", "v2-test", "--test-pr", "4", "--expected-head", new string('a', 40), "--authorization-file", path };

        var exit = await GateCli.RunAsync(args, new GateDependencies(new NoopApi(), TextWriter.Null), output, TextWriter.Null, CancellationToken.None);

        Assert.Equal(3, exit);
        Assert.Contains("authorization_mismatch", output.ToString());
    }

    [Fact]
    public async Task OutputIsStableJsonAndDoesNotEchoSecretsOrRawApiErrors()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var args = new[] { "probe", "--repo", "example/test-repo", "--base", "main", "--expected-check", "build", "--reviewer-app", "reviewer", "--gate-app", "gate", "--merge-app", "merge" };
        var exit = await GateCli.RunAsync(args, new GateDependencies(new ThrowingApi("token-secret raw body"), TextWriter.Null), output, error, CancellationToken.None);

        Assert.Equal(3, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("BLOCKED", document.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("token-secret", output.ToString());
        Assert.DoesNotContain("raw body", output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    private sealed class NoopApi : IGitHubApi
    {
        public Task<BootstrapEvidence> ReadBootstrapEvidenceAsync(BootstrapProbeRequest request, CancellationToken cancellationToken)
            => Task.FromResult(new BootstrapEvidence(true, false, false, false, "reviewer", "gate", "merge", "gate", ["build"], false, true, true, true, true, new string('a', 40), true, true));
    }

    private sealed class ThrowingApi(string message) : IGitHubApi
    {
        public Task<BootstrapEvidence> ReadBootstrapEvidenceAsync(BootstrapProbeRequest request, CancellationToken cancellationToken)
            => Task.FromException<BootstrapEvidence>(new GitHubApiException(message, 500));
    }
}
