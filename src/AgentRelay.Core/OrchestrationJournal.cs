using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRelay.Core;

public sealed record AcceptedDispatchCount(string DeviceId, AgentRelayRole Role, int Count);
public sealed record SchedulerCursor(AgentRelayRole Role, string? DeviceId);
public sealed record TaskDispatchIntent(string IssueUrl, AgentRelayRole Role, string DeviceId, string IdempotencyKey, string RequestHash);
public sealed record ActiveTaskRecord(string TaskId, string IdempotencyKey, string IssueUrl, AgentRelayRole Role, string DeviceId, TaskStatus LastObservedStatus, DateTimeOffset LastObservedAt);
public sealed record ControllerCheckpoint(string IssueUrl, string LifecycleStage, DateTimeOffset UpdatedAt);
public sealed record OrchestrationJournalSnapshot(string SchemaVersion, long Revision, IReadOnlyList<AcceptedDispatchCount> AcceptedDispatchCounts, IReadOnlyList<SchedulerCursor> Cursors, IReadOnlyList<TaskDispatchIntent> PendingIntents, IReadOnlyList<ActiveTaskRecord> ActiveTasks, IReadOnlyList<ControllerCheckpoint> Checkpoints);

public sealed class OrchestrationJournal
{
    public const string Schema = "agentrelay-controller-state/v1";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _directory;
    private readonly string _snapshotPath;
    private readonly string _backupPath;

    public OrchestrationJournal(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = Path.GetFullPath(directory);
        _snapshotPath = Path.Combine(_directory, "snapshot.json");
        _backupPath = Path.Combine(_directory, "snapshot.json.bak");
    }

    public OrchestrationJournalSnapshot Load()
    {
        Directory.CreateDirectory(_directory);
        var primaryExists = File.Exists(_snapshotPath);
        var backupExists = File.Exists(_backupPath);
        if (!primaryExists && !backupExists)
            return EmptySnapshot();

        if (primaryExists)
        {
            try
            {
                return ReadSnapshot(_snapshotPath);
            }
            catch (Exception exception) when (exception is JsonException or IOException or NotSupportedException or ArgumentException)
            {
                if (backupExists)
                {
                    try { return ReadSnapshot(_backupPath); }
                    catch (Exception backupException) when (backupException is JsonException or IOException or NotSupportedException or ArgumentException)
                    {
                        throw new JsonException("Neither orchestration journal snapshot is valid.", backupException);
                    }
                }
                throw new JsonException("Orchestration journal snapshot is invalid and no valid backup exists.", exception);
            }
        }

        return ReadSnapshot(_backupPath);
    }

    public void Save(OrchestrationJournalSnapshot snapshot)
    {
        Validate(snapshot);
        Directory.CreateDirectory(_directory);
        if (File.Exists(_snapshotPath))
            _ = ReadSnapshot(_snapshotPath);

        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        var temporaryPath = Path.Combine(_directory, $"snapshot.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_snapshotPath))
                File.Replace(temporaryPath, _snapshotPath, _backupPath, ignoreMetadataErrors: true);
            else
                File.Move(temporaryPath, _snapshotPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public IDisposable AcquireSingleInstanceLock()
    {
        Directory.CreateDirectory(_directory);
        return new LockLease(new FileStream(Path.Combine(_directory, "controller.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None));
    }

    private static OrchestrationJournalSnapshot ReadSnapshot(string path)
    {
        var snapshot = JsonSerializer.Deserialize<OrchestrationJournalSnapshot>(File.ReadAllBytes(path), JsonOptions)
            ?? throw new JsonException("Orchestration journal snapshot cannot be null.");
        Validate(snapshot);
        return snapshot;
    }

    private static void Validate(OrchestrationJournalSnapshot snapshot)
    {
        if (snapshot is null || snapshot.SchemaVersion != Schema || snapshot.Revision < 0 ||
            snapshot.AcceptedDispatchCounts is null || snapshot.Cursors is null || snapshot.PendingIntents is null ||
            snapshot.ActiveTasks is null || snapshot.Checkpoints is null)
            throw new ArgumentException("Invalid or unsupported orchestration journal snapshot.", nameof(snapshot));

        if (snapshot.AcceptedDispatchCounts.Any(item => item is null || string.IsNullOrWhiteSpace(item.DeviceId) ||
                !Enum.IsDefined(item.Role) || item.Count < 0) ||
            snapshot.AcceptedDispatchCounts.GroupBy(item => (item.DeviceId, item.Role)).Any(group => group.Count() > 1) ||
            snapshot.Cursors.Any(item => item is null || !Enum.IsDefined(item.Role) ||
                (item.DeviceId is not null && string.IsNullOrWhiteSpace(item.DeviceId))) ||
            snapshot.Cursors.GroupBy(item => item.Role).Any(group => group.Count() > 1) ||
            snapshot.PendingIntents.Any(item => item is null || !TaskContract.IsCanonicalIssueUrl(item.IssueUrl) ||
                !Enum.IsDefined(item.Role) || string.IsNullOrWhiteSpace(item.DeviceId) ||
                string.IsNullOrWhiteSpace(item.IdempotencyKey) || string.IsNullOrWhiteSpace(item.RequestHash)) ||
            snapshot.ActiveTasks.Any(item => item is null || string.IsNullOrWhiteSpace(item.TaskId) ||
                string.IsNullOrWhiteSpace(item.IdempotencyKey) || !TaskContract.IsCanonicalIssueUrl(item.IssueUrl) ||
                !Enum.IsDefined(item.Role) || string.IsNullOrWhiteSpace(item.DeviceId) ||
                !Enum.IsDefined(item.LastObservedStatus) || item.LastObservedAt == default) ||
            snapshot.Checkpoints.Any(item => item is null || !TaskContract.IsCanonicalIssueUrl(item.IssueUrl) ||
                string.IsNullOrWhiteSpace(item.LifecycleStage) || item.UpdatedAt == default))
            throw new ArgumentException("Invalid orchestration journal record.", nameof(snapshot));
    }

    private static OrchestrationJournalSnapshot EmptySnapshot() => new(Schema, 0, [], [], [], [], []);

    private sealed class LockLease(FileStream stream) : IDisposable
    {
        public void Dispose() => stream.Dispose();
    }
}
