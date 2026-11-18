using System.Text.Json;

namespace AgentRelay.Gate;

public static class GateCli
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public static async Task<int> RunAsync(string[] args, GateDependencies dependencies, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            await output.WriteLineAsync("Usage: agentrelay-gate probe --repo owner/repo --base ref --expected-check name --reviewer-app app --gate-app app --merge-app app [--active --test-task id --test-pr n --expected-head sha --authorization-file path]");
            return 0;
        }

        if (!StringComparer.Ordinal.Equals(args[0], "probe")) return await WriteAsync(output, 2, "BLOCKED", ["unknown_command"]);
        var parsed = Parse(args.Skip(1).ToArray());
        if (!parsed.Success) return await WriteAsync(output, 2, "BLOCKED", ["invalid_arguments"]);

        var required = new[] { "repo", "base", "reviewer-app", "gate-app", "merge-app" };
        if (required.Any(key => !parsed.Values.TryGetValue(key, out var values) || values.Count != 1) || !parsed.Values.TryGetValue("expected-check", out var expectedChecks) || expectedChecks.Count == 0)
            return await WriteAsync(output, 2, "BLOCKED", ["required_argument_missing"]);

        var request = new BootstrapProbeRequest(parsed.Single("repo")!, parsed.Single("base")!, expectedChecks!, parsed.Single("reviewer-app")!, parsed.Single("gate-app")!, parsed.Single("merge-app")!);
        if (parsed.Flags.Contains("active"))
        {
            var activeKeys = new[] { "test-task", "test-pr", "expected-head", "authorization-file" };
            if (activeKeys.Any(key => string.IsNullOrWhiteSpace(parsed.Single(key)))) return await WriteAsync(output, 2, "BLOCKED", ["active_probe_requires_binding"]);
            if (!int.TryParse(parsed.Single("test-pr"), out var pullRequest) || !IsFullSha(parsed.Single("expected-head")!)) return await WriteAsync(output, 2, "BLOCKED", ["active_probe_requires_binding"]);
            var authorization = await ReadAuthorizationAsync(parsed.Single("authorization-file")!, cancellationToken).ConfigureAwait(false);
            if (authorization is null || !authorization.AllowActiveProbe || authorization.ExpiresAt <= DateTimeOffset.UtcNow ||
                !StringComparer.Ordinal.Equals(authorization.Repository, request.Repository) || authorization.PullRequest != pullRequest ||
                !StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedHead, parsed.Single("expected-head")) ||
                !StringComparer.Ordinal.Equals(authorization.TaskId, parsed.Single("test-task")))
                return await WriteAsync(output, 3, "BLOCKED", ["authorization_mismatch"]);
            return await WriteAsync(output, 3, "BLOCKED", ["active_probe_not_configured"]);
        }

        var result = await new BootstrapProbe().EvaluateAsync(request, dependencies.GitHub, cancellationToken).ConfigureAwait(false);
        return await WriteAsync(output, result.Status == ProbeStatus.Ready ? 0 : 3, result.Status == ProbeStatus.Ready ? "READY" : "BLOCKED", result.Blockers);
    }

    private static async Task<ActiveProbeAuthorization?> ReadAuthorizationAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ActiveProbeAuthorization>(stream, JsonOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return null; }
    }

    private static bool IsFullSha(string value) => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static async Task<int> WriteAsync(TextWriter output, int exitCode, string status, IReadOnlyList<string> blockers)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new { status, blockers }, JsonOptions));
        return exitCode;
    }

    private static ParsedArguments Parse(string[] args)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal)) return new(false, values, flags);
            var key = args[index][2..];
            if (key == "active") { flags.Add(key); continue; }
            if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal)) return new(false, values, flags);
            if (!values.TryGetValue(key, out var list)) values[key] = list = [];
            list.Add(args[index]);
        }
        return new(true, values, flags);
    }

    private sealed record ParsedArguments(bool Success, Dictionary<string, List<string>> Values, HashSet<string> Flags)
    {
        public string? Single(string key) => Values.TryGetValue(key, out var values) && values.Count == 1 ? values[0] : null;
    }
}
