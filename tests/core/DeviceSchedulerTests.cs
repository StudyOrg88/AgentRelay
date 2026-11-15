using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Core.Tests;

public sealed class DeviceSchedulerTests
{
    private const string WindowsRepo = @"C:\work\repo";
    private const string UbuntuRepo = "/work/repo";

    [Fact]
    public void Select_FiltersByEnabledRoleRepositoryPreflightAndConcurrency()
    {
        var devices = new[]
        {
            Device("disabled", enabled: false),
            Device("wrong-role", roles: ["reviewer"]),
            Device("wrong-path", repositories: [@"C:\other"]),
            Device("no-preflight"),
            Device("busy", concurrency: 1),
            Device("eligible", concurrency: 2)
        };

        var result = Select(devices, preflight: new HashSet<string>(["disabled", "wrong-role", "wrong-path", "busy", "eligible"]),
            active: new Dictionary<string, int> { ["busy"] = 1 });

        Assert.Equal("eligible", result.Device?.Id);
    }

    [Fact]
    public void Select_NormalizesSeparatorsAndWindowsCaseButUbuntuIsOrdinal()
    {
        var devices = new[]
        {
            Device("windows", repositories: ["c:/WORK/repo"]),
            Device("ubuntu", os: "ubuntu", repositories: ["/work/Repo"])
        };

        Assert.Equal("windows", Select(devices).Device?.Id);
        Assert.Equal("windows", Select([devices[0]]).Device?.Id);
        Assert.Equal("no_eligible_device", Select([devices[1]], repository: UbuntuRepo).DiagnosticCode);
        Assert.Equal("ubuntu-match", Select([Device("ubuntu-match", os: "ubuntu", repositories: ["/work/repo"])], repository: UbuntuRepo).Device?.Id);
    }

    [Fact]
    public void Select_PrefersLowestAcceptedCountThenRotatesOrdinallyAndWraps()
    {
        var devices = new[] { Device("b"), Device("a"), Device("c") };
        IReadOnlyDictionary<string, int> counts = new Dictionary<string, int> { ["a"] = 2, ["b"] = 1, ["c"] = 1 };

        Assert.Equal("b", Select(devices, counts: counts).Device?.Id);
        Assert.Equal("c", Select(devices, counts: counts, cursor: "b").Device?.Id);
        Assert.Equal("b", Select(devices, counts: counts, cursor: "c").Device?.Id);
    }

    [Fact]
    public void Select_NoEligibleDeviceReturnsDiagnosticAndNullCursor()
    {
        var result = Select([Device("d")], preflight: new HashSet<string>());
        Assert.Null(result.Device);
        Assert.Null(result.NextCursor);
        Assert.Equal("no_eligible_device", result.DiagnosticCode);
    }

    [Fact]
    public void SelectDoesNotMutateCountsAndRecordAcceptedDispatchCopiesAndIncrementsOnlySelectedDevice()
    {
        IReadOnlyDictionary<string, int> counts = new Dictionary<string, int> { ["a"] = 3, ["other"] = 7 };
        _ = Select([Device("a")], counts: counts);
        Assert.Equal(3, counts["a"]);

        var updated = DeviceScheduler.RecordAcceptedDispatch(counts, "a");
        Assert.NotSame(counts, updated);
        Assert.Equal(4, updated["a"]);
        Assert.Equal(7, updated["other"]);
        Assert.Equal(3, counts["a"]);
    }

    private static DeviceSelectionResult Select(
        IReadOnlyList<RegisteredDevice> devices,
        IReadOnlySet<string>? preflight = null,
        IReadOnlyDictionary<string, int>? counts = null,
        IReadOnlyDictionary<string, int>? active = null,
        string? cursor = null,
        string repository = WindowsRepo) =>
        DeviceScheduler.Select(AgentRelayRole.Worker, repository, devices,
            preflight ?? devices.Select(device => device.Id).ToHashSet(StringComparer.Ordinal),
            counts ?? new Dictionary<string, int>(), active ?? new Dictionary<string, int>(), cursor);

    private static RegisteredDevice Device(
        string id,
        bool enabled = true,
        string[]? roles = null,
        string[]? repositories = null,
        string os = "windows",
        int concurrency = 1) =>
        new(id, id, os, "local", null, null, null, repositories ?? [WindowsRepo], roles ?? ["worker"], enabled,
            new Dictionary<string, int> { ["worker"] = concurrency });
}
