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
    string? ExpectedTargetCommit = null);

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
    string Identity);

public sealed record ActiveProbeResult(string EvidenceId, string TargetCommit, string ReviewedHead);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record GateDependencies(IGitHubApi GitHub, TextWriter DiagnosticOutput);
