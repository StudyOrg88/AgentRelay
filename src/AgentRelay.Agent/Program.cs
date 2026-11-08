using System.Text.Json;
using AgentRelay.Core;
using AgentTaskStatus = AgentRelay.Core.TaskStatus;

namespace AgentRelay.Agent;

public sealed class AgentService
{
    private readonly RegisteredDevice _device;
    private readonly TaskStore _store;
    private readonly WorktreeManager _worktrees;
    private readonly ProcessRunner _runner;
    private readonly IChildProcessLauncher? _launcher;

    public AgentService(RegisteredDevice device, TaskStore store, WorktreeManager worktrees, ProcessRunner runner,
        IChildProcessLauncher? launcher = null)
    {
        _device = device;
        _store = store;
        _worktrees = worktrees;
        _runner = runner;
        _launcher = launcher;
    }

    public async Task<string> ExecuteAsync(string operation, string json, CancellationToken cancellationToken = default)
    {
        if (operation is not ("preflight" or "submit" or "status" or "report" or "cancel"))
            throw new JsonException("Unsupported operation.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        object result = operation switch
        {
            "preflight" => Preflight(),
            "submit" => await SubmitAsync(root, cancellationToken).ConfigureAwait(false),
            "status" => Status(RequiredTaskId(root)),
            "report" => Report(RequiredTaskId(root)),
            "cancel" => Cancel(RequiredTaskId(root)),
            _ => throw new JsonException("Unsupported operation.")
        };
        return JsonSerializer.Serialize(result);
    }

    private object Preflight() => new { ok = _device.Enabled, device_id = _device.Id, roles = _device.Roles };

    private async Task<object> SubmitAsync(JsonElement json, CancellationToken cancellationToken)
    {
        var request = TaskRequest.Parse(json.GetRawText());
        using var submissionLease = _store.AcquireSubmissionLease(request.IdempotencyKey);
        if (!_device.Enabled || request.DeviceId != _device.Id || !_device.Roles.Contains(request.Role.ToString().ToLowerInvariant()))
            throw new InvalidOperationException("Task identity, role, or repository is not approved.");
        var existing = _store.FindByIdempotencyKey(request.IdempotencyKey);
        if (existing is not null)
        {
            var repeated = _store.Submit(request);
            if (repeated.Disposition == TaskSubmissionDisposition.Conflict) throw new InvalidOperationException("Idempotency key conflict.");
            var existingRecord = _store.FindByTaskId(repeated.TaskId)!;
            if (!_device.Repositories.Any(path => SamePath(path, existingRecord.Request.Repository)))
                throw new InvalidOperationException("Accepted task repository is no longer approved.");
            return await ResumeOrReturnAsync(existingRecord, cancellationToken).ConfigureAwait(false);
        }

        if (!_device.Repositories.Any(path => SamePath(path, request.Repository)))
            throw new InvalidOperationException("Task identity, role, or repository is not approved.");
        var workspace = _worktrees.ValidateWorkspace(request);

        var accepted = _store.Submit(request);
        if (accepted.Disposition == TaskSubmissionDisposition.Conflict) throw new InvalidOperationException("Idempotency key conflict.");
        if (accepted.Disposition != TaskSubmissionDisposition.Accepted)
        {
            _ = _runner.Reconcile(accepted.TaskId);
            return _store.FindByTaskId(accepted.TaskId)!.Status;
        }
        await ResumeOrStartAsync(request, workspace, cancellationToken).ConfigureAwait(false);
        return _store.FindByTaskId(request.TaskId)!.Status;
    }

    private async Task<TaskStatusRecord> ResumeOrReturnAsync(StoredTaskRecord record, CancellationToken cancellationToken)
    {
        if (record.Status.Status is AgentTaskStatus.Queued or AgentTaskStatus.Starting)
            return await ResumeOrStartAsync(record.Request, _worktrees.ValidateWorkspace(record.Request), cancellationToken).ConfigureAwait(false);
        return _runner.Reconcile(record.Request.TaskId);
    }

    private async Task<TaskStatusRecord> ResumeOrStartAsync(TaskRequest request, string workspace, CancellationToken cancellationToken)
    {
        var record = _store.FindByTaskId(request.TaskId)!;
        if (record.ProcessIdentity is not null)
        {
            if (_runner.GetVerifiedIdentity(request.TaskId) is not null)
            {
                _store.WriteStatus(record.Status with { Status = AgentTaskStatus.Running, UpdatedAt = DateTimeOffset.UtcNow });
                return _store.FindByTaskId(request.TaskId)!.Status;
            }
            var reconciled = _runner.Reconcile(request.TaskId);
            if (reconciled.Status != AgentTaskStatus.Queued && reconciled.Status != AgentTaskStatus.Starting)
                return reconciled;
            if (_runner.HasProcessIdentityFile(request.TaskId))
                throw new InvalidOperationException("Task has a persisted process identity but no verifiable live process or exit marker; refusing duplicate launch.");
        }

        if (record.Status.Status == AgentTaskStatus.Queued)
            _store.WriteStatus(record.Status with { Status = AgentTaskStatus.Starting, UpdatedAt = DateTimeOffset.UtcNow });
        var actualWorkspace = await _worktrees.CreateOrValidateAsync(request, cancellationToken).ConfigureAwait(false);
        if (!SamePath(workspace, actualWorkspace)) throw new InvalidOperationException("Created worktree path did not match the task identity.");
        _runner.Start(request, actualWorkspace, _launcher);
        return _store.FindByTaskId(request.TaskId)!.Status;
    }

    private object Status(string taskId)
    {
        var status = _runner.Reconcile(taskId);
        return new { status, process = _runner.GetVerifiedIdentity(taskId) };
    }

    private object Report(string taskId)
    {
        _ = _runner.Reconcile(taskId);
        return _store.FindByTaskId(taskId)?.Report ?? throw new InvalidOperationException("Task report is not available.");
    }

    private object Cancel(string taskId)
    {
        var cancelled = _runner.Cancel(taskId);
        return new { task_id = taskId, cancelled };
    }

    private static string RequiredTaskId(JsonElement json) => json.ValueKind == JsonValueKind.Object &&
        json.TryGetProperty("task_id", out var id) && id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString())
            ? id.GetString()!
            : throw new JsonException("task_id is required.");

    private static bool SamePath(string left, string right) => string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length != 1) throw new InvalidOperationException("Expected one supported operation.");
            var input = await Console.In.ReadToEndAsync().ConfigureAwait(false);
            var configPath = Environment.GetEnvironmentVariable("AGENTRELAY_CONFIG") ?? Path.Combine(AppContext.BaseDirectory, "agentrelay-agent.json");
            using var configDocument = JsonDocument.Parse(await File.ReadAllTextAsync(configPath).ConfigureAwait(false));
            var config = configDocument.RootElement;
            var repositoryRoot = config.GetProperty("repository_root").GetString()!;
            var registry = DeviceRegistry.Load(config.GetProperty("registry").GetString()!, repositoryRoot);
            var deviceId = config.GetProperty("device_id").GetString()!;
            var device = registry.Devices.SingleOrDefault(entry => entry.Id == deviceId && entry.Enabled)
                ?? throw new InvalidOperationException("Configured device is not registered and enabled.");
            var store = new TaskStore(config.GetProperty("state_directory").GetString()!);
            var worktrees = new WorktreeManager(device.Repositories);
            var runner = new ProcessRunner(store, config.GetProperty("log_directory").GetString()!);
            var service = new AgentService(device, store, worktrees, runner);
            Console.Out.WriteLine(await service.ExecuteAsync(args[0], input).ConfigureAwait(false));
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine("agent_error: " + Sanitize(exception.Message));
            Console.Out.WriteLine("{\"error\":\"request_rejected\"}");
            return 2;
        }
    }

    private static string Sanitize(string message)
    {
        var singleLine = message.Replace('\r', ' ').Replace('\n', ' ');
        return System.Text.RegularExpressions.Regex.Replace(singleLine,
            "(?i)(token|password|secret|authorization)(\\s*[:=]\\s*)[^\\s,;]+", "$1$2[redacted]")[..Math.Min(300, singleLine.Length)];
    }
}
