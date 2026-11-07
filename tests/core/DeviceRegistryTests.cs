using System.Text.Json;
using AgentRelay.Core;
using Xunit;

namespace AgentRelay.Core.Tests;

public sealed class DeviceRegistryTests
{
    [Fact]
    public void Load_accepts_valid_windows_local_record_inside_trusted_root()
    {
        var result = Load("windows", "local", null, null, null, [@"C:\repos\AgentRelay"]);

        var device = Assert.Single(result.Devices);
        Assert.Equal("device-1", device.Id);
        Assert.Equal("windows", device.Os);
        Assert.Equal("local", device.Connection);
        Assert.Equal([@"C:\repos\AgentRelay"], device.Repositories);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Load_accepts_valid_ubuntu_local_record_when_trusted_root_is_posix()
    {
        var result = Load("ubuntu", "local", null, null, null, ["/home/codex/work/AgentRelay"], "/home/codex/work");

        Assert.Single(result.Devices);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Load_accepts_windows_ssh_record()
    {
        var result = Load("windows", "ssh", "fake-windows-host", 2222, "codex-user", [@"C:\repos\AgentRelay"]);

        var device = Assert.Single(result.Devices);
        Assert.Equal("fake-windows-host", device.Host);
        Assert.Equal(2222, device.Port);
        Assert.Equal("codex-user", device.User);
    }

    [Fact]
    public void Load_accepts_windows_unc_repository_with_server_and_share()
    {
        var result = Load("windows", "ssh", "fake-windows-host", 22, "codex-user", [@"\\fake-server\fake-share\AgentRelay"]);

        var device = Assert.Single(result.Devices);
        Assert.Equal([@"\\fake-server\fake-share\AgentRelay"], device.Repositories);
        Assert.Empty(result.Diagnostics);
    }

    [Theory]
    [InlineData(@"\\")]
    [InlineData(@"\\fake-server")]
    public void Load_rejects_windows_unc_repository_without_server_or_share(string repository)
    {
        var result = Load("windows", "ssh", "fake-windows-host", 22, "codex-user", [repository]);

        Assert.Empty(result.Devices);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Load_accepts_ubuntu_ssh_record()
    {
        var result = Load("ubuntu", "ssh", "fake-ubuntu-host", 22, "codex-user", ["/home/codex/work/AgentRelay"]);

        var device = Assert.Single(result.Devices);
        Assert.Equal("ubuntu", device.Os);
        Assert.Equal("fake-ubuntu-host", device.Host);
    }

    [Fact]
    public void Load_defaults_every_declared_role_to_one_when_concurrency_map_is_omitted()
    {
        var entry = Entry().Replace(",\"max_concurrency\":{\"worker\":1,\"reviewer\":1}", "", StringComparison.Ordinal);
        var result = LoadJson(Document(entry));

        var device = Assert.Single(result.Devices);
        Assert.Equal(1, device.MaxConcurrency["worker"]);
        Assert.Equal(1, device.MaxConcurrency["reviewer"]);
    }

    [Fact]
    public void Load_defaults_omitted_declared_role_to_one_when_concurrency_map_is_partial()
    {
        var entry = Entry().Replace("\"worker\":1,\"reviewer\":1", "\"worker\":3", StringComparison.Ordinal);
        var result = LoadJson(Document(entry));

        var device = Assert.Single(result.Devices);
        Assert.Equal(3, device.MaxConcurrency["worker"]);
        Assert.Equal(1, device.MaxConcurrency["reviewer"]);
    }

    [Fact]
    public void Load_rejects_concurrency_for_a_role_the_device_does_not_declare()
    {
        var entry = Entry()
            .Replace("\"worker\",\"reviewer\"", "\"worker\"", StringComparison.Ordinal)
            .Replace("\"worker\":1,\"reviewer\":1", "\"reviewer\":1", StringComparison.Ordinal);
        var result = LoadJson(Document(entry));

        Assert.Empty(result.Devices);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Load_excludes_all_duplicate_ids_and_returns_sanitized_diagnostics()
    {
        const string secret = "ssh-private-secret-value";
        var first = Entry(id: "duplicate", host: secret);
        var second = Entry(id: "duplicate", host: secret);
        var result = LoadJson(Document(first, second));

        Assert.Empty(result.Devices);
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, diagnostic => Assert.DoesNotContain(secret, diagnostic.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("macos", "ssh", "worker")]
    [InlineData("ubuntu", "local", "administrator")]
    [InlineData("ubuntu", "serial", "worker")]
    public void Load_excludes_unknown_os_role_and_connection(string os, string connection, string role)
    {
        var result = LoadJson(Document(Entry(os: os, connection: connection, roles: [role])));

        Assert.Empty(result.Devices);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Load_excludes_entry_with_missing_repository_paths()
    {
        var result = LoadJson("""{"schema_version":"agentrelay-devices/v1","devices":[{"id":"d","name":"Test","os":"ubuntu","connection":"ssh","host":"fake-host","port":22,"user":"codex","roles":["worker"],"enabled":false,"max_concurrency":{"worker":1}}]}""");

        Assert.Empty(result.Devices);
        Assert.Contains(result.Diagnostics, item => item.Code == "invalid_entry");
    }

    [Theory]
    [InlineData(null, 22, "codex")]
    [InlineData("fake-host", null, "codex")]
    [InlineData("fake-host", 22, null)]
    public void Load_excludes_ssh_record_with_missing_connection_fields(string? host, int? port, string? user)
    {
        var result = Load("ubuntu", "ssh", host, port, user, ["/home/codex/work/AgentRelay"]);

        Assert.Empty(result.Devices);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Theory]
    [InlineData("password", "do-not-leak-password")]
    [InlineData("private_key", "do-not-leak-key")]
    [InlineData("token", "do-not-leak-token")]
    [InlineData("mystery", "do-not-leak-value")]
    public void Load_rejects_secret_or_unknown_properties_without_echoing_values(string property, string value)
    {
        var entry = JsonDocument.Parse(Entry()).RootElement.GetRawText();
        var injected = entry[..^1] + $",\"{property}\":\"{value}\"}}";
        var result = LoadJson(Document(injected));

        Assert.Empty(result.Devices);
        Assert.NotEmpty(result.Diagnostics);
        Assert.All(result.Diagnostics, diagnostic =>
        {
            Assert.DoesNotContain(value, diagnostic.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(property, diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Theory]
    [InlineData("relative/repo")]
    [InlineData("/home/codex/../secret")]
    public void Load_rejects_relative_or_traversing_ubuntu_repository_paths(string repository)
    {
        var result = Load("ubuntu", "ssh", "fake-host", 22, "codex", [repository]);

        Assert.Empty(result.Devices);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Load_rejects_windows_ssh_repository_with_parent_segment()
    {
        var result = Load("windows", "ssh", "fake-windows-host", 22, "codex-user", [@"C:\repos\..\secret"]);

        Assert.Empty(result.Devices);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Load_rejects_local_repository_outside_trusted_root()
    {
        var result = Load("windows", "local", null, null, null, [@"C:\outside\Other"]);

        Assert.Empty(result.Devices);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Load_requires_supported_root_schema_and_well_formed_json()
    {
        Assert.ThrowsAny<JsonException>(() => LoadJson("{"));
        Assert.ThrowsAny<JsonException>(() => LoadJson("""{"schema_version":"agentrelay-devices/v1"}"""));
        Assert.Throws<JsonException>(() => LoadJson("""{"schema_version":"agentrelay-devices/v2","devices":[]}"""));
        Assert.Throws<JsonException>(() => LoadJson("""{"schema_version":"agentrelay-devices/v1","devices":[],"unexpected":true}"""));
    }

    private static DeviceRegistryResult Load(
        string os,
        string connection,
        string? host,
        int? port,
        string? user,
        string[] repositories,
        string? repositoryRoot = null)
    {
        var json = Document(Entry(os: os, connection: connection, host: host, port: port, user: user, repositories: repositories));
        return LoadJson(json, repositoryRoot);
    }

    private static DeviceRegistryResult LoadJson(string json, string? repositoryRoot = null)
    {
        var path = Path.Combine(Path.GetTempPath(), $"device-registry-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, json);
        try
        {
            return DeviceRegistry.Load(path, repositoryRoot ?? @"C:\repos");
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string Document(params string[] entries) =>
        $$"""{"schema_version":"agentrelay-devices/v1","devices":[{{string.Join(',', entries)}}]}""";

    private static string Entry(
        string id = "device-1",
        string os = "ubuntu",
        string connection = "ssh",
        string? host = "fake-host",
        int? port = 22,
        string? user = "codex",
        string[]? repositories = null,
        string[]? roles = null) =>
        JsonSerializer.Serialize(new
        {
            id,
            name = "Test device",
            os,
            connection,
            host,
            port,
            user,
            repositories = repositories ?? ["/home/codex/work/AgentRelay"],
            roles = roles ?? ["worker", "reviewer"],
            enabled = false,
            max_concurrency = new { worker = 1, reviewer = 1 }
        });
}
