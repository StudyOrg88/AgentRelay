using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Core.Tests;

public sealed class TaskLifecycleTests
{
    [Fact]
    public void Planned_issue_without_pull_request_routes_to_worker_when_dependencies_are_complete()
    {
        var decision = TaskLifecycle.Decide(new TaskSnapshot(
            "https://github.com/acme/repo/issues/7", ["task:planned"], true, null));

        Assert.Equal(TaskAction.Worker, decision.Action);
    }

    [Fact]
    public void Planned_issue_with_incomplete_dependencies_does_not_route_to_worker()
    {
        var decision = TaskLifecycle.Decide(new TaskSnapshot(
            "https://github.com/acme/repo/issues/7", ["task:planned"], false, null));

        Assert.Equal(TaskAction.NeedsInput, decision.Action);
    }

    [Fact]
    public void Review_with_matching_full_head_and_base_does_not_reroute_reviewer()
    {
        var head = new string('a', 40);
        var @base = new string('b', 40);
        var snapshot = new TaskSnapshot("https://github.com/acme/repo/issues/7", ["task:review"], true, "https://github.com/acme/repo/pull/9")
        {
            PullRequestHeadSha = head,
            PullRequestBaseSha = @base,
            ReviewedHeadSha = head,
            ReviewedBaseSha = @base,
            ReviewResult = "PASS"
        };

        Assert.Equal(TaskAction.NeedsInput, TaskLifecycle.Decide(snapshot).Action);
    }

    [Fact]
    public void Review_with_unchanged_head_but_changed_base_routes_for_new_review()
    {
        var snapshot = new TaskSnapshot("https://github.com/acme/repo/issues/7", ["task:review"], true, "https://github.com/acme/repo/pull/9")
        {
            PullRequestHeadSha = new string('a', 40),
            PullRequestBaseSha = new string('c', 40),
            ReviewedHeadSha = new string('a', 40),
            ReviewedBaseSha = new string('b', 40),
            ReviewResult = "PASS"
        };

        Assert.Equal(TaskAction.Reviewer, TaskLifecycle.Decide(snapshot).Action);
    }

    [Fact]
    public void Matching_changes_requested_review_is_left_for_orchestrator_finding_routing()
    {
        var head = new string('a', 40);
        var @base = new string('b', 40);
        var snapshot = new TaskSnapshot("https://github.com/acme/repo/issues/7", ["task:review"], true, "https://github.com/acme/repo/pull/9")
        {
            PullRequestHeadSha = head,
            PullRequestBaseSha = @base,
            ReviewedHeadSha = head,
            ReviewedBaseSha = @base,
            ReviewResult = "CHANGES_REQUESTED"
        };

        Assert.Equal(TaskAction.NeedsInput, TaskLifecycle.Decide(snapshot).Action);
    }
}
