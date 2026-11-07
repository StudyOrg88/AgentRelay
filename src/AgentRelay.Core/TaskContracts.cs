using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRelay.Core;

public static class TaskContract
{
    public const string SchemaVersion = "agentrelay-task/v1";

    internal static bool IsCanonicalIssueUrl(string? value) =>
        value is not null && System.Text.RegularExpressions.Regex.IsMatch(
            value,
            @"\Ahttps://github\.com/[A-Za-z0-9](?:[A-Za-z0-9-]{0,37}[A-Za-z0-9])?/[A-Za-z0-9_.-]+/issues/[1-9][0-9]*\z",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    internal static void RequireProperties(string json, params string[] required)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
            throw new JsonException("Task contract must be a JSON object.");
        foreach (var property in required)
        {
            if (!document.RootElement.TryGetProperty(property, out _))
                throw new JsonException($"Required task property '{property}' is missing.");
        }
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<AgentRelayRole>))]
public enum AgentRelayRole
{
    Worker,
    Reviewer
}

[JsonConverter(typeof(JsonStringEnumConverter<TaskStatus>))]
public enum TaskStatus
{
    [JsonStringEnumMemberName("queued")]
    Queued,
    [JsonStringEnumMemberName("starting")]
    Starting,
    [JsonStringEnumMemberName("running")]
    Running,
    [JsonStringEnumMemberName("succeeded")]
    Succeeded,
    [JsonStringEnumMemberName("failed")]
    Failed,
    [JsonStringEnumMemberName("cancelled")]
    Cancelled,
    [JsonStringEnumMemberName("needs_input")]
    NeedsInput
}

public sealed record TaskRequest(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("issue_url")] string IssueUrl,
    [property: JsonPropertyName("role")] AgentRelayRole Role,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("repository")] string Repository,
    [property: JsonPropertyName("workspace")] string Workspace,
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("lifecycle_generation")] int LifecycleGeneration)
{
    public static TaskRequest Parse(string json)
    {
        TaskContract.RequireProperties(json, "schema_version", "task_id", "idempotency_key", "issue_url", "role",
            "device_id", "repository", "workspace", "branch", "lifecycle_generation");
        var request = JsonSerializer.Deserialize<TaskRequest>(json, ContractJson.Options)
            ?? throw new JsonException("Task request cannot be null.");
        request.Validate();
        return request;
    }

    public string ToJson()
    {
        Validate();
        return JsonSerializer.Serialize(this, ContractJson.Options);
    }

    public void Validate()
    {
        if (SchemaVersion != TaskContract.SchemaVersion)
            throw new JsonException("Unsupported task schema version.");
        Require(TaskId, nameof(TaskId));
        Require(IdempotencyKey, nameof(IdempotencyKey));
        Require(DeviceId, nameof(DeviceId));
        Require(Repository, nameof(Repository));
        Require(Workspace, nameof(Workspace));
        Require(Branch, nameof(Branch));
        if (!TaskContract.IsCanonicalIssueUrl(IssueUrl))
            throw new JsonException("Issue URL must be a canonical GitHub Issue URL.");
        if (!Enum.IsDefined(Role))
            throw new JsonException("Unsupported task role.");
        if (LifecycleGeneration < 0)
            throw new JsonException("Lifecycle generation must be nonnegative.");
    }

    private static void Require(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new JsonException($"Required task identity field '{field}' is missing.");
    }

}

public sealed record TaskStatusRecord(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("status")] TaskStatus Status,
    [property: JsonPropertyName("updated_at")] DateTimeOffset UpdatedAt)
{
    public static TaskStatusRecord Parse(string json)
    {
        TaskContract.RequireProperties(json, "schema_version", "task_id", "idempotency_key", "device_id", "status", "updated_at");
        var record = JsonSerializer.Deserialize<TaskStatusRecord>(json, ContractJson.Options)
            ?? throw new JsonException("Task status cannot be null.");
        if (record.SchemaVersion != TaskContract.SchemaVersion || string.IsNullOrWhiteSpace(record.TaskId) ||
            string.IsNullOrWhiteSpace(record.IdempotencyKey) || string.IsNullOrWhiteSpace(record.DeviceId) ||
            !Enum.IsDefined(record.Status) || record.UpdatedAt == default)
            throw new JsonException("Invalid or unsupported task status record.");
        return record;
    }

    public string ToJson() => JsonSerializer.Serialize(this, ContractJson.Options);
}

public sealed record TaskReport(
    [property: JsonPropertyName("schema_version")] string SchemaVersion,
    [property: JsonPropertyName("task_id")] string TaskId,
    [property: JsonPropertyName("idempotency_key")] string IdempotencyKey,
    [property: JsonPropertyName("issue_url")] string IssueUrl,
    [property: JsonPropertyName("role")] AgentRelayRole Role,
    [property: JsonPropertyName("device_id")] string DeviceId,
    [property: JsonPropertyName("status")] TaskStatus Status,
    [property: JsonPropertyName("started_at")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("completed_at")] DateTimeOffset? CompletedAt,
    [property: JsonPropertyName("branch")] string? Branch,
    [property: JsonPropertyName("commit_sha")] string? CommitSha,
    [property: JsonPropertyName("evidence")] IReadOnlyList<string> Evidence,
    [property: JsonPropertyName("findings")] IReadOnlyList<string> Findings,
    [property: JsonPropertyName("blockers")] IReadOnlyList<string> Blockers,
    [property: JsonPropertyName("log_reference")] string? LogReference)
{
    public static TaskReport Parse(string json)
    {
        TaskContract.RequireProperties(json, "schema_version", "task_id", "idempotency_key", "issue_url", "role",
            "device_id", "status", "started_at", "evidence", "findings", "blockers");
        var report = JsonSerializer.Deserialize<TaskReport>(json, ContractJson.Options)
            ?? throw new JsonException("Task report cannot be null.");
        if (report.SchemaVersion != TaskContract.SchemaVersion || string.IsNullOrWhiteSpace(report.TaskId) ||
            string.IsNullOrWhiteSpace(report.IdempotencyKey) || string.IsNullOrWhiteSpace(report.DeviceId) ||
            !TaskContract.IsCanonicalIssueUrl(report.IssueUrl) || !Enum.IsDefined(report.Role) ||
            !Enum.IsDefined(report.Status) || report.StartedAt == default || report.Evidence is null ||
            report.Findings is null || report.Blockers is null)
            throw new JsonException("Invalid or unsupported task report.");
        return report;
    }

    public string ToJson() => JsonSerializer.Serialize(this, ContractJson.Options);
}

internal static class ContractJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
}
