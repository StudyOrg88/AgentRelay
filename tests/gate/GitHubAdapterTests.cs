using AgentRelay.Gate;

namespace AgentRelay.Gate.Tests;

public sealed class GitHubAdapterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 19, 3, 0, 0, TimeSpan.Zero);
    private const string Sandbox = "StudyOrg88/AgentRelay-v2-sandbox";
    private static readonly string Head = new('a', 40);

    [Fact]
    public async Task ReadOnlyRejectsNonAllowlistedRepositoryBeforeTransport()
    {
        var transport = new RecordingBootstrapTransport();
        var api = new AllowlistedGitHubApi(transport, new FixedClock(Now));
        var request = new BootstrapProbeRequest("StudyOrg88/AgentRelay", "main", ["sandbox-contract"], "reviewer", "gate", "merge");

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => api.ReadBootstrapEvidenceAsync(request, CancellationToken.None));

        Assert.Equal("repository_not_allowlisted", error.SafeReason);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task EmptyOrForbiddenReadOnlyEvidenceFailsClosedWithoutRawError()
    {
        var transport = new RecordingBootstrapTransport { ReadException = new GitHubTransportException("token-secret raw body", 403) };
        var api = new AllowlistedGitHubApi(transport, new FixedClock(Now));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => api.ReadBootstrapEvidenceAsync(ReadOnlyRequest(), CancellationToken.None));

        Assert.Equal("github_evidence_unavailable", error.SafeReason);
        Assert.DoesNotContain("token-secret", error.Message);
        Assert.DoesNotContain("raw body", error.Message);
    }

    public static IEnumerable<object[]> InvalidActiveAuthorizations()
    {
        yield return [ValidAuthorization() with { Repository = "StudyOrg88/AgentRelay" }, "repository_not_allowlisted"];
        yield return [ValidAuthorization() with { TaskId = "other-task" }, "authorization_mismatch"];
        yield return [ValidAuthorization() with { PullRequest = 2 }, "authorization_mismatch"];
        yield return [ValidAuthorization() with { ExpectedHead = new string('b', 40) }, "authorization_mismatch"];
        yield return [ValidAuthorization() with { ExpiresAt = Now }, "authorization_expired"];
        yield return [ValidAuthorization() with { AllowedActions = [ActiveProbeAction.GateCheck] }, "action_not_authorized"];
        yield return [ValidAuthorization() with { ReviewerIdentity = "other-reviewer" }, "identity_mismatch"];
    }

    [Theory]
    [MemberData(nameof(InvalidActiveAuthorizations))]
    public async Task ActiveWritesRequireExactShortLivedBinding(ActiveProbeAuthorization authorization, string reason)
    {
        var transport = new RecordingBootstrapTransport();
        var api = new AllowlistedGitHubApi(transport, new FixedClock(Now));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => api.ExecuteActiveAsync(ActiveRequest(), authorization, CancellationToken.None));

        Assert.Equal(reason, error.SafeReason);
        Assert.Empty(transport.Requests);
    }

    [Fact]
    public async Task AuthorizedNativeReviewUsesOnlyBoundOperation()
    {
        var transport = new RecordingBootstrapTransport { ActiveResult = new ActiveProbeResult("review-id", Head, Head) };
        var api = new AllowlistedGitHubApi(transport, new FixedClock(Now));

        var result = await api.ExecuteActiveAsync(ActiveRequest(), ValidAuthorization(), CancellationToken.None);

        Assert.Equal("review-id", result.EvidenceId);
        var request = Assert.Single(transport.Requests);
        Assert.Equal(ActiveProbeAction.NativeReview, request.Action);
        Assert.Equal(Sandbox, request.Repository);
        Assert.Equal(1, request.PullRequest);
        Assert.Equal(Head, request.ExpectedHead);
        Assert.Equal("reviewer-app", request.Identity);
    }

    public static IEnumerable<object[]> InvalidActiveResults()
    {
        yield return [new ActiveProbeResult("", Head, Head), "active_evidence_invalid"];
        yield return [new ActiveProbeResult("evidence-id", "abc123", Head), "active_target_invalid"];
        yield return [new ActiveProbeResult("evidence-id", new string('b', 40), Head), "active_target_mismatch"];
        yield return [new ActiveProbeResult("evidence-id", Head, "abc123"), "reviewed_head_invalid"];
        yield return [new ActiveProbeResult("evidence-id", Head, new string('b', 40)), "reviewed_head_mismatch"];
    }

    [Theory]
    [MemberData(nameof(InvalidActiveResults))]
    public async Task ActiveResultMustContainBoundVerifiableEvidence(ActiveProbeResult activeResult, string reason)
    {
        var transport = new RecordingBootstrapTransport { ActiveResult = activeResult };
        var api = new AllowlistedGitHubApi(transport, new FixedClock(Now));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => api.ExecuteActiveAsync(ActiveRequest(), ValidAuthorization(), CancellationToken.None));

        Assert.Equal(reason, error.SafeReason);
    }

    [Theory]
    [InlineData(ActiveProbeAction.NativeReview, "reviewer-app")]
    [InlineData(ActiveProbeAction.GateCheck, "gate-app")]
    [InlineData(ActiveProbeAction.LeaseCompareAndSwap, "orchestrator-app")]
    [InlineData(ActiveProbeAction.WorkerNegativePermission, "worker-app")]
    public async Task EachActionRequiresItsBoundIdentity(ActiveProbeAction action, string identity)
    {
        var transport = new RecordingBootstrapTransport { ActiveResult = new ActiveProbeResult("evidence-id", Head, Head) };
        var api = new AllowlistedGitHubApi(transport, new FixedClock(Now));
        var request = ActiveRequest() with { Action = action, Identity = identity };

        await api.ExecuteActiveAsync(request, ValidAuthorization(), CancellationToken.None);

        Assert.Equal(action, Assert.Single(transport.Requests).Action);
    }

    [Fact]
    public async Task ActiveTransportFailureIsRedactedAndFailsClosed()
    {
        var transport = new RecordingBootstrapTransport { ActiveException = new GitHubTransportException("secret installation token", 500) };
        var api = new AllowlistedGitHubApi(transport, new FixedClock(Now));

        var error = await Assert.ThrowsAsync<GitHubApiException>(() => api.ExecuteActiveAsync(ActiveRequest(), ValidAuthorization(), CancellationToken.None));

        Assert.Equal("github_write_unavailable", error.SafeReason);
        Assert.DoesNotContain("secret", error.Message);
        Assert.DoesNotContain("token", error.Message);
    }

    private static BootstrapProbeRequest ReadOnlyRequest()
        => new(Sandbox, "main", ["sandbox-contract"], "reviewer-app", "gate-app", "merge-app");

    private static ActiveProbeRequest ActiveRequest()
        => new(Sandbox, "v2_bootstrap_probe", 1, Head, Head, ActiveProbeAction.NativeReview, "reviewer-app");

    private static ActiveProbeAuthorization ValidAuthorization()
        => new(Sandbox, 1, Head, Now.AddMinutes(30), true, "v2_bootstrap_probe",
            [ActiveProbeAction.NativeReview, ActiveProbeAction.GateCheck, ActiveProbeAction.LeaseCompareAndSwap, ActiveProbeAction.WorkerNegativePermission],
            "reviewer-app", "gate-app", "merge-app", "worker-app", "orchestrator-app", Head);

    private sealed class FixedClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }

    private sealed class RecordingBootstrapTransport : IGitHubBootstrapTransport
    {
        public List<ActiveProbeRequest> Requests { get; } = [];
        public GitHubTransportException? ReadException { get; init; }
        public GitHubTransportException? ActiveException { get; init; }
        public BootstrapEvidence? ReadResult { get; init; }
        public ActiveProbeResult? ActiveResult { get; init; }

        public Task<BootstrapEvidence?> ReadEvidenceAsync(BootstrapProbeRequest request, CancellationToken cancellationToken)
            => ReadException is null ? Task.FromResult(ReadResult) : Task.FromException<BootstrapEvidence?>(ReadException);

        public Task<ActiveProbeResult?> ExecuteActiveAsync(ActiveProbeRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return ActiveException is null ? Task.FromResult(ActiveResult) : Task.FromException<ActiveProbeResult?>(ActiveException);
        }
    }
}
