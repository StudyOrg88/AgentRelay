using System.Text.Json;
using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Core.Tests;

public sealed class TaskContractsTests
{
    private const string ValidJson = """
        {
          "schema_version":"agentrelay-task/v1",
          "task_id":"task-123",
          "idempotency_key":"repo-issue-role-generation",
          "issue_url":"https://github.com/acme/project/issues/42",
          "role":"Worker",
          "device_id":"device-a",
          "repository":"C:/repos/project",
          "workspace":"C:/work/task-123",
          "branch":"task/123",
          "lifecycle_generation":1
        }
        """;

    [Fact]
    public void Request_round_trips_v1_json_with_explicit_names()
    {
        var request = TaskRequest.Parse(ValidJson);

        Assert.Equal(TaskContract.SchemaVersion, request.SchemaVersion);
        Assert.Equal(request, TaskRequest.Parse(request.ToJson()));
        Assert.Contains("\"lifecycle_generation\"", request.ToJson());
    }

    [Fact]
    public void Request_requires_all_identity_fields()
    {
        var missingTaskId = ValidJson.Replace("\"task_id\":\"task-123\",", "");
        Assert.ThrowsAny<JsonException>(() => TaskRequest.Parse(missingTaskId));
        Assert.ThrowsAny<JsonException>(() => TaskRequest.Parse(ValidJson.Replace("task-123", " ")));
        Assert.ThrowsAny<JsonException>(() => TaskRequest.Parse(ValidJson.Replace("\"lifecycle_generation\":1", "")));
        Assert.Equal(0, TaskRequest.Parse(ValidJson.Replace("\"lifecycle_generation\":1", "\"lifecycle_generation\":0")).LifecycleGeneration);
    }

    [Fact]
    public void Request_rejects_unsupported_schema_and_unknown_fields()
    {
        Assert.Throws<JsonException>(() => TaskRequest.Parse(ValidJson.Replace("agentrelay-task/v1", "agentrelay-task/v2")));
        Assert.Throws<JsonException>(() => TaskRequest.Parse(ValidJson.Replace("\"task_id\":\"task-123\",", "\"task_id\":\"task-123\",\"secret\":\"must-not-be-accepted\",")));
    }

    [Theory]
    [InlineData("https://GITHUB.com/acme/project/issues/42")]
    [InlineData("https://github.com/acme/pro%6Aect/issues/42")]
    [InlineData("https://github.com/acme/project/issues/42/")]
    [InlineData("https://github.com/acme/project/issues/042")]
    [InlineData("")]
    public void Request_rejects_noncanonical_issue_urls(string issueUrl)
    {
        Assert.ThrowsAny<JsonException>(() => TaskRequest.Parse(ValidJson.Replace(
            "https://github.com/acme/project/issues/42", issueUrl)));
    }

    [Fact]
    public void Status_values_are_exactly_the_v1_values()
    {
        var expected = new[] { "queued", "starting", "running", "succeeded", "failed", "cancelled", "needs_input" };
        var statuses = Enum.GetValues<TaskStatus>();
        Assert.Equal(expected, statuses.Select(status =>
            JsonDocument.Parse(new TaskStatusRecord(TaskContract.SchemaVersion, "task", "key", "device", status,
                DateTimeOffset.UnixEpoch).ToJson()).RootElement.GetProperty("status").GetString()));
        foreach (var value in expected)
        {
            var json = $$"""{"schema_version":"agentrelay-task/v1","task_id":"t","idempotency_key":"k","device_id":"d","status":"{{value}}","updated_at":"1970-01-01T00:00:00+00:00"}""";
            var parsed = TaskStatusRecord.Parse(json);
            using var serialized = JsonDocument.Parse(parsed.ToJson());
            Assert.Equal(value, serialized.RootElement.GetProperty("status").GetString());
            Assert.Equal(Enum.GetValues<TaskStatus>().Single(status =>
                JsonDocument.Parse(new TaskStatusRecord(TaskContract.SchemaVersion, "task", "key", "device", status,
                    DateTimeOffset.UnixEpoch).ToJson()).RootElement.GetProperty("status").GetString() == value), parsed.Status);
        }
    }

    [Fact]
    public void Status_record_requires_every_wire_property()
    {
        var valid = """{"schema_version":"agentrelay-task/v1","task_id":"t","idempotency_key":"k","device_id":"d","status":"queued","updated_at":"1970-01-01T00:00:00+00:00"}""";
        foreach (var property in new[] { "schema_version", "task_id", "idempotency_key", "device_id", "status", "updated_at" })
        {
            var missing = System.Text.RegularExpressions.Regex.Replace(valid, $",?\"{property}\":\"[^\"]*\",?", "");
            Assert.ThrowsAny<JsonException>(() => TaskStatusRecord.Parse(missing));
        }
        var missingStatus = valid.Replace("\"status\":\"queued\",", "");
        Assert.ThrowsAny<JsonException>(() => TaskStatusRecord.Parse(missingStatus));
    }

    [Fact]
    public void Report_requires_identity_and_supported_schema()
    {
        var report = new TaskReport(TaskContract.SchemaVersion, "task-123", "key-1",
            "https://github.com/acme/project/issues/42", AgentRelayRole.Reviewer, "device-b",
            TaskStatus.Succeeded, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "task/123", "abc123",
            ["tests passed"], [], [], null);
        var parsed = TaskReport.Parse(report.ToJson());
        Assert.Equal(report.TaskId, parsed.TaskId);
        Assert.Equal(report.IdempotencyKey, parsed.IdempotencyKey);
        Assert.Equal(report.Status, parsed.Status);
        Assert.Equal(report.Evidence, parsed.Evidence);
        Assert.Equal(report.Findings, parsed.Findings);
        Assert.Equal(report.Blockers, parsed.Blockers);
        Assert.Throws<JsonException>(() => TaskReport.Parse(report.ToJson().Replace("\"task_id\":\"task-123\",", "")));
        Assert.Throws<JsonException>(() => TaskReport.Parse(report.ToJson().Replace("agentrelay-task/v1", "agentrelay-task/v2")));
        Assert.ThrowsAny<JsonException>(() => TaskReport.Parse(report.ToJson().Replace(report.IssueUrl, "")));
        Assert.ThrowsAny<JsonException>(() => TaskReport.Parse(report.ToJson().Replace(report.IssueUrl, "https://github.com/acme/project/issues/%34%32")));
        foreach (var property in new[] { "schema_version", "task_id", "idempotency_key", "issue_url", "role", "device_id", "status", "started_at" })
        {
            var missing = System.Text.RegularExpressions.Regex.Replace(report.ToJson(), $",?\"{property}\":(?:\"[^\"]*\"|\"[^\"]*\"),?", "");
            Assert.ThrowsAny<JsonException>(() => TaskReport.Parse(missing));
        }
    }

    private static string ToWireValue(TaskStatus status) => status switch
    {
        TaskStatus.Queued => "queued",
        TaskStatus.Starting => "starting",
        TaskStatus.Running => "running",
        TaskStatus.Succeeded => "succeeded",
        TaskStatus.Failed => "failed",
        TaskStatus.Cancelled => "cancelled",
        TaskStatus.NeedsInput => "needs_input",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };
}
