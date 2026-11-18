using AgentRelay.Gate;

namespace AgentRelay.Gate.Tests;

public sealed class BootstrapProbeTests
{
    [Fact]
    public async Task ReadOnlyProbePassesOnlyWithCompleteIsolatedEvidence()
    {
        var result = await new BootstrapProbe().EvaluateAsync(ValidRequest(), new FakeGitHubApi(ValidEvidence()), CancellationToken.None);

        Assert.Equal(ProbeStatus.Ready, result.Status);
        Assert.Empty(result.Blockers);
    }

    public static IEnumerable<object[]> InvalidEvidence()
    {
        yield return [ValidEvidence() with { ExpectedChecks = [] }, "expected_checks_empty"];
        yield return [ValidEvidence() with { PermissionsKnown = false }, "permissions_unknown"];
        yield return [ValidEvidence() with { WorkerCanMerge = true }, "worker_can_merge"];
        yield return [ValidEvidence() with { WorkerCanBypass = true }, "worker_can_bypass"];
        yield return [ValidEvidence() with { WorkerCanWriteControlRefs = true }, "worker_can_write_control_refs"];
        yield return [ValidEvidence() with { ReviewerIdentity = "gate-app" }, "identities_not_isolated"];
        yield return [ValidEvidence() with { GateCheckApp = "wrong-app" }, "gate_app_mismatch"];
        yield return [ValidEvidence() with { MergeQueueEnabled = true }, "merge_queue_unsupported"];
    }

    [Theory]
    [MemberData(nameof(InvalidEvidence))]
    public async Task ReadOnlyProbeFailsClosed(BootstrapEvidence evidence, string blocker)
    {
        var result = await new BootstrapProbe().EvaluateAsync(ValidRequest(), new FakeGitHubApi(evidence), CancellationToken.None);

        Assert.Equal(ProbeStatus.Blocked, result.Status);
        Assert.Contains(blocker, result.Blockers);
    }

    [Fact]
    public async Task ApiForbiddenIsBlockedNotReady()
    {
        var result = await new BootstrapProbe().EvaluateAsync(ValidRequest(), new FakeGitHubApi(new GitHubApiException("forbidden", 403)), CancellationToken.None);

        Assert.Equal(ProbeStatus.Blocked, result.Status);
        Assert.Contains("github_evidence_unavailable", result.Blockers);
        Assert.DoesNotContain("forbidden", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    private static BootstrapProbeRequest ValidRequest() => new("example/test-repo", "main", ["build"], "reviewer-app", "gate-app", "merge-app");

    private static BootstrapEvidence ValidEvidence() => new(
        PermissionsKnown: true,
        WorkerCanMerge: false,
        WorkerCanBypass: false,
        WorkerCanWriteControlRefs: false,
        ReviewerIdentity: "reviewer-app",
        GateIdentity: "gate-app",
        MergeIdentity: "merge-app",
        GateCheckApp: "gate-app",
        ExpectedChecks: ["build"],
        MergeQueueEnabled: false);

    private sealed class FakeGitHubApi : IGitHubApi
    {
        private readonly BootstrapEvidence? _evidence;
        private readonly Exception? _exception;

        public FakeGitHubApi(BootstrapEvidence evidence) => _evidence = evidence;
        public FakeGitHubApi(Exception exception) => _exception = exception;

        public Task<BootstrapEvidence> ReadBootstrapEvidenceAsync(BootstrapProbeRequest request, CancellationToken cancellationToken)
            => _exception is null ? Task.FromResult(_evidence!) : Task.FromException<BootstrapEvidence>(_exception);
    }
}
