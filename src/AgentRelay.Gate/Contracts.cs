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
    bool MergeQueueEnabled);

public sealed record BootstrapProbeResult(ProbeStatus Status, IReadOnlyList<string> Blockers, string Summary);

public sealed record ActiveProbeAuthorization(
    string Repository,
    int PullRequest,
    string ExpectedHead,
    DateTimeOffset ExpiresAt,
    bool AllowActiveProbe,
    string? TaskId = null);

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed record GateDependencies(IGitHubApi GitHub, TextWriter DiagnosticOutput);
