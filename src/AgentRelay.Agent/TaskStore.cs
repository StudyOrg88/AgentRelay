using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentRelay.Core;
using AgentTaskStatus = AgentRelay.Core.TaskStatus;

namespace AgentRelay.Agent;

public enum TaskSubmissionDisposition { Accepted, AlreadyAccepted, Conflict }
public sealed record TaskSubmissionResult(TaskSubmissionDisposition Disposition, string TaskId, TaskStatusRecord Status);
public sealed record StoredTaskRecord(string SchemaVersion, string RequestHash, TaskRequest Request, TaskStatusRecord Status, TaskReport? Report);

public sealed class TaskStore
{
    public const string Schema = "agentrelay-task-store/v1";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };
    private readonly string _directory;

    public TaskStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
    }

    public TaskSubmissionResult Submit(TaskRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        Directory.CreateDirectory(_directory);
        var keyHash = Hash(request.IdempotencyKey);
        using var lease = AcquireKeyLock(keyHash);
        var path = RecordPath(keyHash);
        var requestJson = request.ToJson();
        var requestHash = Hash(requestJson);
        if (File.Exists(path))
        {
            var existing = ReadRecord(path);
            if (existing.Request.IdempotencyKey != request.IdempotencyKey)
                throw new JsonException("Task record key hash does not match its request.");
            return new TaskSubmissionResult(
                existing.RequestHash == requestHash ? TaskSubmissionDisposition.AlreadyAccepted : TaskSubmissionDisposition.Conflict,
                existing.Request.TaskId, existing.Status);
        }

        var status = new TaskStatusRecord(TaskContract.SchemaVersion, request.TaskId, request.IdempotencyKey,
            request.DeviceId, AgentTaskStatus.Queued, DateTimeOffset.UtcNow);
        var record = new StoredTaskRecord(Schema, requestHash, request, status, null);
        WriteRecord(path, record);
        return new TaskSubmissionResult(TaskSubmissionDisposition.Accepted, request.TaskId, status);
    }

    public TaskSubmissionResult? FindByIdempotencyKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var path = RecordPath(Hash(key));
        if (!File.Exists(path))
            return null;
        var record = ReadRecord(path);
        if (record.Request.IdempotencyKey != key)
            throw new JsonException("Task record key hash does not match its request.");
        return new TaskSubmissionResult(TaskSubmissionDisposition.AlreadyAccepted, record.Request.TaskId, record.Status);
    }

    public StoredTaskRecord? FindByTaskId(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        Directory.CreateDirectory(_directory);
        StoredTaskRecord? match = null;
        foreach (var path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            var record = ReadRecord(path);
            if (record.Request.TaskId != taskId)
                continue;
            if (match is not null)
                throw new JsonException("Multiple task records use the same task ID.");
            match = record;
        }
        return match;
    }

    public void WriteStatus(TaskStatusRecord status)
    {
        ArgumentNullException.ThrowIfNull(status);
        status = TaskStatusRecord.Parse(status.ToJson());
        UpdateRecord(status.IdempotencyKey, record =>
        {
            if (record.Request.TaskId != status.TaskId || record.Request.DeviceId != status.DeviceId)
                throw new InvalidOperationException("Task status identity does not match the accepted request.");
            return record with { Status = status };
        });
    }

    public void WriteReport(TaskReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        report = TaskReport.Parse(report.ToJson());
        UpdateRecord(report.IdempotencyKey, record =>
        {
            if (record.Request.TaskId != report.TaskId || record.Request.IssueUrl != report.IssueUrl ||
                record.Request.Role != report.Role || record.Request.DeviceId != report.DeviceId)
                throw new InvalidOperationException("Task report identity does not match the accepted request.");
            return record with { Report = report };
        });
    }

    private void UpdateRecord(string key, Func<StoredTaskRecord, StoredTaskRecord> update)
    {
        Directory.CreateDirectory(_directory);
        var hash = Hash(key);
        using var lease = AcquireKeyLock(hash);
        var path = RecordPath(hash);
        if (!File.Exists(path))
            throw new InvalidOperationException("No accepted task exists for the supplied idempotency key.");
        var record = ReadRecord(path);
        if (record.Request.IdempotencyKey != key)
            throw new JsonException("Task record key hash does not match its request.");
        WriteRecord(path, update(record));
    }

    private StoredTaskRecord ReadRecord(string path)
    {
        var record = JsonSerializer.Deserialize<StoredTaskRecord>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new JsonException("Stored task record cannot be null.");
        if (record.SchemaVersion != Schema || string.IsNullOrWhiteSpace(record.RequestHash) ||
            record.Request is null || record.Status is null)
            throw new JsonException("Invalid or unsupported task store schema.");
        record.Request.Validate();
        var expectedHash = Hash(record.Request.ToJson());
        if (record.RequestHash != expectedHash || record.Status.TaskId != record.Request.TaskId ||
            record.Status.IdempotencyKey != record.Request.IdempotencyKey || record.Status.DeviceId != record.Request.DeviceId)
            throw new JsonException("Stored task record identity or request hash is invalid.");
        _ = TaskStatusRecord.Parse(record.Status.ToJson());
        if (record.Report is not null)
        {
            var report = TaskReport.Parse(record.Report.ToJson());
            if (report.TaskId != record.Request.TaskId || report.IdempotencyKey != record.Request.IdempotencyKey ||
                report.IssueUrl != record.Request.IssueUrl || report.Role != record.Request.Role ||
                report.DeviceId != record.Request.DeviceId)
                throw new JsonException("Stored task report identity is invalid.");
        }
        return record;
    }

    private void WriteRecord(string path, StoredTaskRecord record)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
        var temporaryPath = Path.Combine(_directory, $"{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private FileStream AcquireKeyLock(string keyHash)
    {
        var lockPath = Path.Combine(_directory, $"{keyHash}.lock");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            try { return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) when (DateTime.UtcNow < deadline) { Thread.Sleep(10); }
        }
    }

    private string RecordPath(string keyHash) => Path.Combine(_directory, $"{keyHash}.json");

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
