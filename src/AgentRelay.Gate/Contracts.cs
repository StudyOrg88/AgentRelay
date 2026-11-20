namespace AgentRelay.Gate;

public enum ProbeStatus { Ready, Blocked }

public sealed record BootstrapProbeRequest(
    string Repository,
    string Base,
    IReadOnlyList<string> ExpectedChecks,
    string ReviewerApp,
    string GateApp,
    string MergeApp);

public sealed record BootstrapEvidence(
    bool PermissionsKnown,
    bool WorkerCanMerge,
    bool WorkerCanBypass,
    bool WorkerCanWriteControlRefs,
    string ReviewerIdentity,
    string GateIdentity,
    string MergeIdentity,
    string GateCheckApp,
    IReadOnlyList<string> ExpectedChecks,
    bool MergeQueueEnabled,
    bool LeaseCapabilityKnown,
    bool LeaseCreateUsesNonForceCas,
    bool LeaseCompetitionSingleWinner,
    bool GateTargetKnown,
    string? GateTargetCommit,
    bool GateTargetsRequiredCommit,
    bool GateTargetAssociatedWithReviewedHead);

public sealed record BootstrapProbeResult(ProbeStatus Status, IReadOnlyList<string> Blockers, string Summary);

public sealed record ActiveProbeAuthorization(
    string Repository,
    int PullRequest,
    string ExpectedHead,
    DateTimeOffset ExpiresAt,
    bool AllowActiveProbe,
    string? TaskId = null,
    IReadOnlyList<ActiveProbeAction>? AllowedActions = null,
    string? ReviewerIdentity = null,
    string? GateIdentity = null,
    string? MergeIdentity = null,
    string? WorkerIdentity = null,
    string? OrchestratorIdentity = null,
    string? ExpectedTargetCommit = null,
    string? ExpectedReference = null,
    string? ExpectedParent = null,
    IReadOnlyList<string>? ExpectedContenders = null,
    string? Evidence = null,
    string? ExpectedBaseSha = null);

public enum ActiveProbeAction
{
    NativeReview,
    GateCheck,
    LeaseCompareAndSwap,
    WorkerNegativePermission
}

public sealed record ActiveProbeRequest(
    string Repository,
    string TaskId,
    int PullRequest,
    string ExpectedHead,
    string ExpectedTargetCommit,
    ActiveProbeAction Action,
    string Identity,
    string? Reference = null,
    string? ExpectedParent = null,
    IReadOnlyList<string>? Contenders = null,
    string? Evidence = null);

public sealed record ActiveProbeResult(
    string EvidenceId,
    string TargetCommit,
    string ReviewedHead,
    string? Reference = null,
    string? ExpectedParent = null,
    IReadOnlyList<string>? Contenders = null,
    string? Evidence = null);

public static class ActiveProbeEvidence
{
    public static string Digest(string value)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed record StageOneGitHubConfig(
    long RepositoryId,
    int PullRequest,
    string HeadSha,
    string TestMergeSha,
    string RequiredCheck,
    long RequiredCheckAppId,
    long RulesetId,
    long ReviewerAppId,
    long GateAppId,
    long MergeAppId,
    long WorkerAppId,
    long OrchestratorAppId,
    string ControlReference,
    string LeaseReference);

public sealed record StageOneGitHubCredentials(
    string ConfigurationToken,
    IReadOnlyDictionary<long, string> InstallationScopeTokens);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record GateDependencies(IGitHubApi GitHub, TextWriter DiagnosticOutput);
