namespace AgentRelay.Gate;

public interface IGitHubApi
{
    Task<BootstrapEvidence> ReadBootstrapEvidenceAsync(BootstrapProbeRequest request, CancellationToken cancellationToken);
}

public interface IActiveGitHubApi
{
    Task<ActiveProbeResult> ExecuteActiveAsync(
        ActiveProbeRequest request,
        ActiveProbeAuthorization authorization,
        CancellationToken cancellationToken);
}

public sealed class GitHubApiException : Exception
{
    public GitHubApiException(string safeReason, int? statusCode = null) : base("GitHub operation failed.")
    {
        SafeReason = safeReason;
        StatusCode = statusCode;
    }

    public string SafeReason { get; }
    public int? StatusCode { get; }
}

public sealed class GitHubTransportException : Exception
{
    public GitHubTransportException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;
    public int? StatusCode { get; }
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public interface IGitHubBootstrapTransport
{
    Task<BootstrapEvidence?> ReadEvidenceAsync(BootstrapProbeRequest request, CancellationToken cancellationToken);
    Task<ActiveProbeResult?> ExecuteActiveAsync(ActiveProbeRequest request, CancellationToken cancellationToken);
}

public sealed class AllowlistedGitHubApi : IGitHubApi, IActiveGitHubApi
{
    public const string SandboxRepository = "StudyOrg88/AgentRelay-v2-sandbox";

    private readonly IGitHubBootstrapTransport _transport;
    private readonly IClock _clock;

    public AllowlistedGitHubApi(IGitHubBootstrapTransport transport, IClock clock)
    {
        _transport = transport;
        _clock = clock;
    }

    public async Task<BootstrapEvidence> ReadBootstrapEvidenceAsync(BootstrapProbeRequest request, CancellationToken cancellationToken)
    {
        EnsureAllowlisted(request.Repository);
        try
        {
            return await _transport.ReadEvidenceAsync(request, cancellationToken).ConfigureAwait(false)
                ?? throw new GitHubApiException("github_evidence_unavailable");
        }
        catch (GitHubApiException) { throw; }
        catch (GitHubTransportException error)
        {
            throw new GitHubApiException("github_evidence_unavailable", error.StatusCode);
        }
    }

    public async Task<ActiveProbeResult> ExecuteActiveAsync(
        ActiveProbeRequest request,
        ActiveProbeAuthorization authorization,
        CancellationToken cancellationToken)
    {
        EnsureAllowlisted(request.Repository);
        EnsureAllowlisted(authorization.Repository);
        ValidateAuthorization(request, authorization);

        try
        {
            var result = await _transport.ExecuteActiveAsync(request, cancellationToken).ConfigureAwait(false)
                ?? throw new GitHubApiException("github_write_unavailable");
            ValidateActiveResult(request, result);
            return result;
        }
        catch (GitHubApiException) { throw; }
        catch (GitHubTransportException error)
        {
            throw new GitHubApiException("github_write_unavailable", error.StatusCode);
        }
    }

    private static void EnsureAllowlisted(string repository)
    {
        if (!StringComparer.Ordinal.Equals(repository, SandboxRepository))
            throw new GitHubApiException("repository_not_allowlisted");
    }

    private void ValidateAuthorization(ActiveProbeRequest request, ActiveProbeAuthorization authorization)
    {
        if (!authorization.AllowActiveProbe ||
            !StringComparer.Ordinal.Equals(authorization.Repository, request.Repository) ||
            !StringComparer.Ordinal.Equals(authorization.TaskId, request.TaskId) ||
            authorization.PullRequest != request.PullRequest ||
            !IsFullSha(request.ExpectedHead) ||
            !StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedHead, request.ExpectedHead) ||
            !IsFullSha(request.ExpectedTargetCommit) ||
            !StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedTargetCommit, request.ExpectedTargetCommit))
            throw new GitHubApiException("authorization_mismatch");

        if (authorization.ExpiresAt <= _clock.UtcNow)
            throw new GitHubApiException("authorization_expired");

        if (authorization.AllowedActions is null || !authorization.AllowedActions.Contains(request.Action))
            throw new GitHubApiException("action_not_authorized");

        if (!StringComparer.Ordinal.Equals(ExpectedIdentity(request.Action, authorization), request.Identity))
            throw new GitHubApiException("identity_mismatch");
    }

    private static string? ExpectedIdentity(ActiveProbeAction action, ActiveProbeAuthorization authorization) => action switch
    {
        ActiveProbeAction.NativeReview => authorization.ReviewerIdentity,
        ActiveProbeAction.GateCheck => authorization.GateIdentity,
        ActiveProbeAction.LeaseCompareAndSwap => authorization.OrchestratorIdentity,
        ActiveProbeAction.WorkerNegativePermission => authorization.WorkerIdentity,
        _ => null
    };

    private static void ValidateActiveResult(ActiveProbeRequest request, ActiveProbeResult result)
    {
        if (string.IsNullOrWhiteSpace(result.EvidenceId))
            throw new GitHubApiException("active_evidence_invalid");
        if (!IsFullSha(result.TargetCommit))
            throw new GitHubApiException("active_target_invalid");
        if (!StringComparer.OrdinalIgnoreCase.Equals(result.TargetCommit, request.ExpectedTargetCommit))
            throw new GitHubApiException("active_target_mismatch");
        if (!IsFullSha(result.ReviewedHead))
            throw new GitHubApiException("reviewed_head_invalid");
        if (!StringComparer.OrdinalIgnoreCase.Equals(result.ReviewedHead, request.ExpectedHead))
            throw new GitHubApiException("reviewed_head_mismatch");
    }

    private static bool IsFullSha(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);
}

// The first delivery intentionally has no ambient high-privilege implementation.
// Real identities are wired only after the separately authorized test-repository bootstrap.
public sealed class FailClosedGitHubApi : IGitHubApi
{
    public Task<BootstrapEvidence> ReadBootstrapEvidenceAsync(BootstrapProbeRequest request, CancellationToken cancellationToken)
        => Task.FromException<BootstrapEvidence>(new GitHubApiException("bootstrap identity evidence is not configured"));
}
