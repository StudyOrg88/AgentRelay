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
}
