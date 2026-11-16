namespace AgentRelay.Core;

public enum TaskAction
{
    Worker,
    NeedsInput
}

public sealed record TaskSnapshot(
    string IssueUrl,
    IReadOnlyList<string> Labels,
    bool DependenciesReady,
    string? PullRequestEvidenceId);

public sealed record LifecycleDecision(TaskAction Action, string Reason);

public static class TaskLifecycle
{
    public static LifecycleDecision Decide(TaskSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!AgentRelay.Core.TaskContract.IsCanonicalIssueUrl(snapshot.IssueUrl))
            return new(TaskAction.NeedsInput, "Issue identity is not canonical.");

        var lifecycleLabels = snapshot.Labels
            .Where(label => label is "task:planned" or "task:blocked" or "task:in-progress" or "task:review" or "task:done" or "task:failed")
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (lifecycleLabels.Length != 1)
            return new(TaskAction.NeedsInput, "Lifecycle labels are missing or contradictory.");

        if (lifecycleLabels[0] is "task:blocked" or "task:failed")
            return new(TaskAction.NeedsInput, "Issue is blocked or failed.");
        if (lifecycleLabels[0] == "task:planned")
            return snapshot.DependenciesReady
                ? new(TaskAction.Worker, "Planned task dependencies are complete.")
                : new(TaskAction.NeedsInput, "Task dependencies are not complete.");
        if (lifecycleLabels[0] == "task:in-progress")
            return new(TaskAction.Worker, snapshot.PullRequestEvidenceId is null
                ? "Resume the in-progress task; no matching PR exists yet."
                : "Resume the in-progress task using its matching PR evidence.");

        return new(TaskAction.NeedsInput, "Issue is not eligible for initial Worker routing.");
    }
}
