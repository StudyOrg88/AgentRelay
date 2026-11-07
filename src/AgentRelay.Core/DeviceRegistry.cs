using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRelay.Core;

public sealed record RegisteredDevice(
    string Id,
    string Name,
    string Os,
    string Connection,
    string? Host,
    int? Port,
    string? User,
    IReadOnlyList<string> Repositories,
    IReadOnlyList<string> Roles,
    bool Enabled,
    IReadOnlyDictionary<string, int> MaxConcurrency);

public sealed record DeviceRegistryDiagnostic(string Code, string Message);

public sealed record DeviceRegistryResult(
    IReadOnlyList<RegisteredDevice> Devices,
    IReadOnlyList<DeviceRegistryDiagnostic> Diagnostics);

public static class DeviceRegistry
{
    public const string SchemaVersion = "agentrelay-devices/v1";

    private static readonly HashSet<string> RootProperties = ["schema_version", "devices"];
    private static readonly HashSet<string> DeviceProperties =
    [
        "id", "name", "os", "connection", "host", "port", "user", "repositories", "roles", "enabled", "max_concurrency"
    ];
    private static readonly HashSet<string> ValidRoles = ["worker", "reviewer"];
    private static readonly Regex SafeIdentifier = new(@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant);

    public static DeviceRegistryResult Load(string path, string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("Device registry root must be an object.");
        EnsureUniqueAndKnownProperties(root, RootProperties, "Device registry root");
        RequireRootProperties(root, "schema_version", "devices");
        if (root.GetProperty("schema_version").ValueKind != JsonValueKind.String ||
            root.GetProperty("schema_version").GetString() != SchemaVersion)
            throw new JsonException("Unsupported device registry schema version.");
        var entries = root.GetProperty("devices");
        if (entries.ValueKind != JsonValueKind.Array)
            throw new JsonException("Device registry devices must be an array.");

        var rawEntries = entries.EnumerateArray().ToArray();
        var duplicateIds = FindDuplicateIds(rawEntries);
        var devices = new List<RegisteredDevice>();
        var diagnostics = new List<DeviceRegistryDiagnostic>();

        for (var index = 0; index < rawEntries.Length; index++)
        {
            var entry = rawEntries[index];
            if (entry.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(InvalidEntry(index));
                continue;
            }

            try
            {
                if (TryGetString(entry, "id", out var id) && duplicateIds.Contains(id))
                    throw new InvalidEntryException();
                devices.Add(ParseDevice(entry, repositoryRoot));
            }
            catch (InvalidEntryException)
            {
                diagnostics.Add(InvalidEntry(index));
            }
            catch (JsonException)
            {
                diagnostics.Add(InvalidEntry(index));
            }
            catch (ArgumentException)
            {
                diagnostics.Add(InvalidEntry(index));
            }
        }

        return new DeviceRegistryResult(devices.AsReadOnly(), diagnostics.AsReadOnly());
    }

    private static RegisteredDevice ParseDevice(JsonElement entry, string repositoryRoot)
    {
        EnsureUniqueAndKnownProperties(entry, DeviceProperties, "Device entry");
        RequireProperties(entry, "id", "name", "os", "connection", "host", "port", "user", "repositories",
            "roles", "enabled");

        var id = RequiredString(entry, "id");
        var name = RequiredString(entry, "name");
        var os = RequiredString(entry, "os");
        var connection = RequiredString(entry, "connection");
        if (!SafeIdentifier.IsMatch(id) || string.IsNullOrWhiteSpace(name) || os is not ("windows" or "ubuntu") ||
            connection is not ("local" or "ssh"))
            throw new InvalidEntryException();

        var hostElement = entry.GetProperty("host");
        var portElement = entry.GetProperty("port");
        var userElement = entry.GetProperty("user");
        string? host;
        int? port;
        string? user;
        if (connection == "local")
        {
            if (hostElement.ValueKind != JsonValueKind.Null || portElement.ValueKind != JsonValueKind.Null ||
                userElement.ValueKind != JsonValueKind.Null)
                throw new InvalidEntryException();
            host = user = null;
            port = null;
        }
        else
        {
            host = ReadNullableString(hostElement);
            user = ReadNullableString(userElement);
            if (host is null || user is null || !SafeIdentifier.IsMatch(host) || !SafeIdentifier.IsMatch(user) ||
                portElement.ValueKind != JsonValueKind.Number || !portElement.TryGetInt32(out var parsedPort) ||
                parsedPort is < 1 or > 65535)
                throw new InvalidEntryException();
            port = parsedPort;
        }

        var repositories = ReadStringArray(entry.GetProperty("repositories"));
        if (repositories.Count == 0 || repositories.Any(repository =>
                !IsAbsoluteTargetPath(repository, os) || (connection == "local" && !IsContained(repository, repositoryRoot, os))))
            throw new InvalidEntryException();

        var roles = ReadStringArray(entry.GetProperty("roles"));
        if (roles.Count == 0 || roles.Distinct(StringComparer.Ordinal).Count() != roles.Count ||
            roles.Any(role => !ValidRoles.Contains(role)))
            throw new InvalidEntryException();

        var enabledElement = entry.GetProperty("enabled");
        if (enabledElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidEntryException();

        var concurrency = roles.ToDictionary(role => role, _ => 1, StringComparer.Ordinal);
        if (entry.TryGetProperty("max_concurrency", out var concurrencyElement))
        {
            if (concurrencyElement.ValueKind != JsonValueKind.Object)
                throw new InvalidEntryException();
            EnsureUniqueAndKnownProperties(concurrencyElement, ValidRoles, "Concurrency map");
            foreach (var property in concurrencyElement.EnumerateObject())
            {
                if (!concurrency.ContainsKey(property.Name) || property.Value.ValueKind != JsonValueKind.Number ||
                    !property.Value.TryGetInt32(out var limit) || limit < 1)
                    throw new InvalidEntryException();
                concurrency[property.Name] = limit;
            }
        }

        return new RegisteredDevice(id, name, os, connection, host, port, user,
            repositories.AsReadOnly(), roles.AsReadOnly(), enabledElement.GetBoolean(),
            new System.Collections.ObjectModel.ReadOnlyDictionary<string, int>(concurrency));
    }

    private static HashSet<string> FindDuplicateIds(IEnumerable<JsonElement> entries)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            if (entry.ValueKind == JsonValueKind.Object && TryGetString(entry, "id", out var id))
                counts[id] = counts.GetValueOrDefault(id) + 1;
        }
        return counts.Where(pair => pair.Value > 1).Select(pair => pair.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsAbsoluteTargetPath(string path, string os)
    {
        if (path.Contains('\0') || HasParentSegment(path, os))
            return false;
        if (os == "ubuntu")
            return path.StartsWith('/') && !path.Contains('\\');
        return IsWindowsAbsolute(path);
    }

    private static bool IsContained(string candidate, string root, string os)
    {
        if (!IsAbsoluteTargetPath(root, os))
            return false;
        if (os == "ubuntu")
        {
            var normalizedRoot = root.TrimEnd('/');
            if (normalizedRoot.Length == 0)
                normalizedRoot = "/";
            return candidate == normalizedRoot || normalizedRoot == "/" ||
                candidate.StartsWith(normalizedRoot + "/", StringComparison.Ordinal);
        }

        var normalizedCandidate = NormalizeWindowsPath(candidate).TrimEnd('\\');
        var normalizedRootWindows = NormalizeWindowsPath(root).TrimEnd('\\');
        return normalizedCandidate.Equals(normalizedRootWindows, StringComparison.OrdinalIgnoreCase) ||
            normalizedCandidate.StartsWith(normalizedRootWindows + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWindowsAbsolute(string path)
    {
        var normalized = path.Replace('/', '\\');
        if (Regex.IsMatch(normalized, @"\A[A-Za-z]:\\", RegexOptions.CultureInvariant))
            return true;
        if (!normalized.StartsWith("\\\\", StringComparison.Ordinal))
            return false;

        var components = normalized[2..].Split('\\');
        return components.Length >= 2 && components[0].Length > 0 && components[1].Length > 0;
    }

    private static bool HasParentSegment(string path, string os)
    {
        var segments = os == "windows" ? path.Replace('/', '\\').Split('\\') : path.Split('/');
        return segments.Any(segment => segment == "..");
    }

    private static string NormalizeWindowsPath(string path) => path.Replace('/', '\\');

    private static List<string> ReadStringArray(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidEntryException();
        var values = new List<string>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidEntryException();
            values.Add(item.GetString()!);
        }
        return values;
    }

    private static string RequiredString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidEntryException();
        return value.GetString()!;
    }

    private static string? ReadNullableString(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null => null,
        JsonValueKind.String => string.IsNullOrWhiteSpace(element.GetString()) ? null : element.GetString(),
        _ => null
    };

    private static bool TryGetString(JsonElement element, string property, out string value)
    {
        if (element.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String)
        {
            value = item.GetString()!;
            return true;
        }
        value = string.Empty;
        return false;
    }

    private static void RequireProperties(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
            if (!element.TryGetProperty(property, out _))
                throw new InvalidEntryException();
    }

    private static void RequireRootProperties(JsonElement element, params string[] properties)
    {
        foreach (var property in properties)
            if (!element.TryGetProperty(property, out _))
                throw new JsonException("Device registry root is missing required properties.");
    }

    private static void EnsureUniqueAndKnownProperties(JsonElement element, HashSet<string> allowed, string description)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!seen.Add(property.Name) || !allowed.Contains(property.Name))
                throw new JsonException($"{description} contains unsupported properties.");
    }

    private static DeviceRegistryDiagnostic InvalidEntry(int index) =>
        new("invalid_entry", $"Device entry at index {index} was rejected.");

    private sealed class InvalidEntryException : Exception;
}
