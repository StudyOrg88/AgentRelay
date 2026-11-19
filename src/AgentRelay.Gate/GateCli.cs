using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentRelay.Gate;

public static class GateCli
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    public static async Task<int> RunAsync(string[] args, GateDependencies dependencies, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || args[0] is "--help" or "-h")
        {
            await output.WriteLineAsync("Usage: agentrelay-gate probe --repo owner/repo --base ref --expected-check name --reviewer-app app --gate-app app --merge-app app [--live-sandbox --stage-one-config path] [--active --test-task id --test-pr n --expected-head sha --authorization-file path --active-action action --active-identity identity --active-target sha --evidence text --active-ref ref --expected-parent sha --contenders sha,sha]");
            return 0;
        }

        if (!StringComparer.Ordinal.Equals(args[0], "probe")) return await WriteAsync(output, 2, "BLOCKED", ["unknown_command"]);
        var parsed = Parse(RemoveRuntimeOptions(args.Skip(1).ToArray()));
        if (!parsed.Success) return await WriteAsync(output, 2, "BLOCKED", ["invalid_arguments"]);

        var required = new[] { "repo", "base", "reviewer-app", "gate-app", "merge-app" };
        if (required.Any(key => !parsed.Values.TryGetValue(key, out var values) || values.Count != 1) || !parsed.Values.TryGetValue("expected-check", out var expectedChecks) || expectedChecks.Count == 0)
            return await WriteAsync(output, 2, "BLOCKED", ["required_argument_missing"]);

        var request = new BootstrapProbeRequest(parsed.Single("repo")!, parsed.Single("base")!, expectedChecks!, parsed.Single("reviewer-app")!, parsed.Single("gate-app")!, parsed.Single("merge-app")!);
        if (parsed.Flags.Contains("active"))
        {
            var activeKeys = new[] { "test-task", "test-pr", "expected-head", "authorization-file", "active-action", "active-identity", "active-target" };
            if (activeKeys.Any(key => string.IsNullOrWhiteSpace(parsed.Single(key)))) return await WriteAsync(output, 2, "BLOCKED", ["active_probe_requires_binding"]);
            if (!int.TryParse(parsed.Single("test-pr"), out var pullRequest) || !IsFullSha(parsed.Single("expected-head")!) || !IsFullSha(parsed.Single("active-target")!)) return await WriteAsync(output, 2, "BLOCKED", ["active_probe_requires_binding"]);
            if (!TryParseAction(parsed.Single("active-action")!, out var action)) return await WriteAsync(output, 2, "BLOCKED", ["active_probe_requires_binding"]);
            var actionArgumentsValid = action switch
            {
                ActiveProbeAction.NativeReview or ActiveProbeAction.GateCheck => !string.IsNullOrWhiteSpace(parsed.Single("evidence")),
                ActiveProbeAction.LeaseCompareAndSwap => parsed.Single("active-ref") == "refs/heads/agentrelay-locks/v2_bootstrap_probe" && IsFullSha(parsed.Single("expected-parent")) && ParseContenders(parsed.Single("contenders")) is { Count: 2 } contenders && contenders.All(IsFullSha),
                ActiveProbeAction.WorkerNegativePermission => parsed.Single("active-ref") == "refs/heads/agentrelay-control" && !string.IsNullOrWhiteSpace(parsed.Single("evidence")),
                _ => false
            };
            if (!actionArgumentsValid) return await WriteAsync(output, 2, "BLOCKED", ["active_action_arguments_invalid"]);
            var authorization = await ReadAuthorizationAsync(parsed.Single("authorization-file")!, cancellationToken).ConfigureAwait(false);
            if (authorization is null || !authorization.AllowActiveProbe || authorization.ExpiresAt <= DateTimeOffset.UtcNow ||
                !StringComparer.Ordinal.Equals(authorization.Repository, request.Repository) || authorization.PullRequest != pullRequest ||
                !StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedHead, parsed.Single("expected-head")) ||
                !StringComparer.Ordinal.Equals(authorization.TaskId, parsed.Single("test-task")) ||
                authorization.AllowedActions is not { Count: 1 } || !authorization.AllowedActions.Contains(action) ||
                !StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedTargetCommit, parsed.Single("active-target")) ||
                !StringComparer.Ordinal.Equals(authorization.ExpectedReference, parsed.Single("active-ref")) ||
                !StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedParent, parsed.Single("expected-parent")) ||
                !(authorization.ExpectedContenders ?? []).SequenceEqual(ParseContenders(parsed.Single("contenders")) ?? [], StringComparer.OrdinalIgnoreCase) ||
                !StringComparer.Ordinal.Equals(authorization.Evidence, parsed.Single("evidence")))
                return await WriteAsync(output, 3, "BLOCKED", ["authorization_mismatch"]);
            if (dependencies.GitHub is not IActiveGitHubApi activeApi)
                return await WriteAsync(output, 3, "BLOCKED", ["active_probe_not_configured"]);

            var activeRequest = new ActiveProbeRequest(
                request.Repository,
                parsed.Single("test-task")!,
                pullRequest,
                parsed.Single("expected-head")!,
                parsed.Single("active-target")!,
                action,
                parsed.Single("active-identity")!,
                parsed.Single("active-ref"),
                parsed.Single("expected-parent"),
                ParseContenders(parsed.Single("contenders")),
                parsed.Single("evidence"));
            try
            {
                var activeResult = await activeApi.ExecuteActiveAsync(activeRequest, authorization, cancellationToken).ConfigureAwait(false);
                return await WriteActiveSuccessAsync(output, activeRequest, activeResult);
            }
            catch (GitHubApiException exception)
            {
                return await WriteAsync(output, 3, "BLOCKED", [exception.SafeReason]);
            }
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

    private static bool IsFullSha(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    private static bool TryParseAction(string value, out ActiveProbeAction action)
    {
        action = value switch
        {
            "native-review" => ActiveProbeAction.NativeReview,
            "gate-check" => ActiveProbeAction.GateCheck,
            "lease-compare-and-swap" => ActiveProbeAction.LeaseCompareAndSwap,
            "worker-negative-permission" => ActiveProbeAction.WorkerNegativePermission,
            _ => default
        };
        return value is "native-review" or "gate-check" or "lease-compare-and-swap" or "worker-negative-permission";
    }

    private static IReadOnlyList<string>? ParseContenders(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = false };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static async Task<int> WriteAsync(TextWriter output, int exitCode, string status, IReadOnlyList<string> blockers)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new { status, blockers }, JsonOptions));
        return exitCode;
    }

    private static async Task<int> WriteActiveSuccessAsync(TextWriter output, ActiveProbeRequest request, ActiveProbeResult result)
    {
        await output.WriteLineAsync(JsonSerializer.Serialize(new
        {
            status = "ACTIVE_COMPLETED",
            action = request.Action,
            evidence = new
            {
                id = result.EvidenceId,
                targetCommit = result.TargetCommit,
                reviewedHead = result.ReviewedHead,
                reference = result.Reference,
                expectedParent = result.ExpectedParent,
                contenders = result.Contenders,
                details = result.Evidence
            }
        }, JsonOptions));
        return 0;
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

    private static string[] RemoveRuntimeOptions(string[] args)
    {
        var result = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--live-sandbox") continue;
            if (args[index] == "--stage-one-config") { index++; continue; }
            result.Add(args[index]);
        }
        return result.ToArray();
    }

    private sealed record ParsedArguments(bool Success, Dictionary<string, List<string>> Values, HashSet<string> Flags)
    {
        public string? Single(string key) => Values.TryGetValue(key, out var values) && values.Count == 1 ? values[0] : null;
    }
}
