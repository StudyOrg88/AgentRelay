namespace AgentRelay.Gate;

public interface IGitHubApi
{
    Task<BootstrapEvidence> ReadBootstrapEvidenceAsync(BootstrapProbeRequest request, CancellationToken cancellationToken);
}

public sealed class GitHubApiException : Exception
{
    public GitHubApiException(string message, int? statusCode = null) : base(message) => StatusCode = statusCode;
    public int? StatusCode { get; }
}

// The first delivery intentionally has no ambient high-privilege implementation.
// Real identities are wired only after the separately authorized test-repository bootstrap.
public sealed class FailClosedGitHubApi : IGitHubApi
{
    public Task<BootstrapEvidence> ReadBootstrapEvidenceAsync(BootstrapProbeRequest request, CancellationToken cancellationToken)
        => Task.FromException<BootstrapEvidence>(new GitHubApiException("bootstrap identity evidence is not configured"));
}
