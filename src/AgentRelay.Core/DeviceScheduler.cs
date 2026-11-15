namespace AgentRelay.Core;

public sealed record DeviceSelectionResult(
    RegisteredDevice? Device,
    string? NextCursor,
    string? DiagnosticCode);

public static class DeviceScheduler
{
    public static DeviceSelectionResult Select(
        AgentRelayRole role,
        string repositoryPath,
        IReadOnlyList<RegisteredDevice> devices,
        IReadOnlySet<string> preflightSucceededDeviceIds,
        IReadOnlyDictionary<string, int> acceptedDispatchCountsForRole,
        IReadOnlyDictionary<string, int> activeTaskCountsForRole,
        string? cursor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(devices);
        ArgumentNullException.ThrowIfNull(preflightSucceededDeviceIds);
        ArgumentNullException.ThrowIfNull(acceptedDispatchCountsForRole);
        ArgumentNullException.ThrowIfNull(activeTaskCountsForRole);

        var roleName = role switch
        {
            AgentRelayRole.Worker => "worker",
            AgentRelayRole.Reviewer => "reviewer",
            _ => throw new ArgumentOutOfRangeException(nameof(role))
        };

        var eligible = devices.Where(device => device.Enabled && device.Roles.Contains(roleName, StringComparer.Ordinal) &&
            preflightSucceededDeviceIds.Contains(device.Id) &&
            device.MaxConcurrency.TryGetValue(roleName, out var limit) &&
            activeTaskCountsForRole.GetValueOrDefault(device.Id) < limit &&
            device.Repositories.Any(path => PathsMatch(path, repositoryPath, device.Os)));

        var candidates = eligible.ToArray();
        if (candidates.Length == 0)
            return new DeviceSelectionResult(null, null, "no_eligible_device");

        var minimum = candidates.Min(device => acceptedDispatchCountsForRole.GetValueOrDefault(device.Id));
        var tied = candidates.Where(device => acceptedDispatchCountsForRole.GetValueOrDefault(device.Id) == minimum)
            .OrderBy(device => device.Id, StringComparer.Ordinal)
            .ToArray();
        var selected = tied.FirstOrDefault(device => cursor is not null &&
            StringComparer.Ordinal.Compare(device.Id, cursor) > 0) ?? tied[0];
        return new DeviceSelectionResult(selected, selected.Id, null);
    }

    public static IReadOnlyDictionary<string, int> RecordAcceptedDispatch(
        IReadOnlyDictionary<string, int> countsForRole,
        string deviceId)
    {
        ArgumentNullException.ThrowIfNull(countsForRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        var updated = new Dictionary<string, int>(countsForRole, StringComparer.Ordinal)
        {
            [deviceId] = checked(countsForRole.GetValueOrDefault(deviceId) + 1)
        };
        return updated;
    }

    private static bool PathsMatch(string registered, string requested, string os)
    {
        if (os == "windows")
            return string.Equals(registered.Replace('/', '\\'), requested.Replace('/', '\\'), StringComparison.OrdinalIgnoreCase);
        if (os == "ubuntu")
            return string.Equals(registered.Replace('\\', '/'), requested.Replace('\\', '/'), StringComparison.Ordinal);
        return false;
    }
}
