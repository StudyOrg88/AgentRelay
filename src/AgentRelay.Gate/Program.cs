namespace AgentRelay.Gate;

public static class Program
{
    public static Task<int> Main(string[] args)
        => GateCli.RunAsync(args, new GateDependencies(CreateGitHubApi(args, Environment.GetEnvironmentVariable), Console.Error), Console.Out, Console.Error, CancellationToken.None);

    public static IGitHubApi CreateGitHubApi(string[] args, Func<string, string?> getEnvironmentVariable)
    {
        if (!args.Contains("--live-sandbox", StringComparer.Ordinal)) return new FailClosedGitHubApi();
        var configPathIndex = Array.IndexOf(args, "--stage-one-config");
        if (configPathIndex < 0 || configPathIndex + 1 >= args.Length) return new FailClosedGitHubApi();

        try
        {
            var config = System.Text.Json.JsonSerializer.Deserialize<StageOneGitHubConfig>(File.ReadAllText(args[configPathIndex + 1]), JsonOptions);
            if (config is null || !IsApprovedStageOneConfig(config)) return new FailClosedGitHubApi();
            string? token;
            ActiveProbeAuthorization? activeAuthorization = null;
            var active = args.Contains("--active", StringComparer.Ordinal);
            if (active)
            {
                var authorizationIndex = Array.IndexOf(args, "--authorization-file");
                if (authorizationIndex < 0 || authorizationIndex + 1 >= args.Length) return new FailClosedGitHubApi();
                var authorization = System.Text.Json.JsonSerializer.Deserialize<ActiveProbeAuthorization>(File.ReadAllText(args[authorizationIndex + 1]), JsonOptions);
                if (authorization is null || !authorization.AllowActiveProbe || authorization.ExpiresAt <= DateTimeOffset.UtcNow ||
                    !StringComparer.Ordinal.Equals(authorization.Repository, AllowlistedGitHubApi.SandboxRepository) || authorization.PullRequest != config.PullRequest ||
                    !StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedHead, config.HeadSha) || authorization.AllowedActions is not { Count: > 0 } ||
                    !args.Contains("--test-task", StringComparer.Ordinal) || !args.Contains("--test-pr", StringComparer.Ordinal) || !args.Contains("--expected-head", StringComparer.Ordinal) ||
                    !args.Contains("--active-target", StringComparer.Ordinal) || !args.Contains("--active-identity", StringComparer.Ordinal))
                    return new FailClosedGitHubApi();
                activeAuthorization = authorization;

                var actionIndex = Array.IndexOf(args, "--active-action");
                if (actionIndex < 0 || actionIndex + 1 >= args.Length) return new FailClosedGitHubApi();
                var credentialName = args[actionIndex + 1] switch
                {
                    "native-review" => "AGENTRELAY_TOKEN_REVIEWER",
                    "gate-check" => "AGENTRELAY_TOKEN_GATE",
                    "lease-compare-and-swap" => "AGENTRELAY_TOKEN_ORCHESTRATOR",
                    "worker-negative-permission" => "AGENTRELAY_TOKEN_WORKER_1",
                    _ => null
                };
                if (credentialName is null) return new FailClosedGitHubApi();
                var action = args[actionIndex + 1] switch
                {
                    "native-review" => ActiveProbeAction.NativeReview,
                    "gate-check" => ActiveProbeAction.GateCheck,
                    "lease-compare-and-swap" => ActiveProbeAction.LeaseCompareAndSwap,
                    "worker-negative-permission" => ActiveProbeAction.WorkerNegativePermission,
                    _ => (ActiveProbeAction?)null
                };
                if (action is null || authorization.AllowedActions.Count != 1 || !authorization.AllowedActions.Contains(action.Value) || !HasValidActionBinding(args, action.Value, config, authorization)) return new FailClosedGitHubApi();
                var identityIndex = Array.IndexOf(args, "--active-identity");
                var identity = identityIndex >= 0 && identityIndex + 1 < args.Length ? args[identityIndex + 1] : null;
                var task = ArgumentValue(args, "--test-task");
                var pr = ArgumentValue(args, "--test-pr");
                var head = ArgumentValue(args, "--expected-head");
                var target = ArgumentValue(args, "--active-target");
                var activePr = int.TryParse(pr, out var parsedPr) ? parsedPr : -1;
                var validTarget = action == ActiveProbeAction.GateCheck
                    ? StringComparer.OrdinalIgnoreCase.Equals(target, config.HeadSha) || StringComparer.OrdinalIgnoreCase.Equals(target, config.TestMergeSha)
                    : StringComparer.OrdinalIgnoreCase.Equals(target, config.HeadSha);
                if (!StringComparer.Ordinal.Equals(task, authorization.TaskId) || activePr != config.PullRequest || !StringComparer.OrdinalIgnoreCase.Equals(head, config.HeadSha) ||
                    !validTarget || !StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedTargetCommit, target)) return new FailClosedGitHubApi();
                var expectedIdentity = action switch
                {
                    ActiveProbeAction.NativeReview => authorization.ReviewerIdentity,
                    ActiveProbeAction.GateCheck => authorization.GateIdentity,
                    ActiveProbeAction.LeaseCompareAndSwap => authorization.OrchestratorIdentity,
                    ActiveProbeAction.WorkerNegativePermission => authorization.WorkerIdentity,
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(identity) || !StringComparer.Ordinal.Equals(identity, expectedIdentity)) return new FailClosedGitHubApi();
                token = getEnvironmentVariable(credentialName);
                if (string.IsNullOrWhiteSpace(token)) return new FailClosedGitHubApi();
            }
            else
            {
                token = getEnvironmentVariable("AGENTRELAY_TOKEN_SANDBOX_CONFIG");
                if (string.IsNullOrWhiteSpace(token)) return new FailClosedGitHubApi();
            }

            var client = new HttpClient();
            var rest = new GitHubRestBootstrapTransport(client, token, config, active, activeAuthorization);
            return new AllowlistedGitHubApi(rest, new SystemClock());
        }
        catch (Exception) { return new FailClosedGitHubApi(); }
    }

    private static string? ArgumentValue(string[] args, string option)
    {
        var index = Array.IndexOf(args, option);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static bool HasValidActionBinding(string[] args, ActiveProbeAction action, StageOneGitHubConfig config, ActiveProbeAuthorization authorization)
    {
        static bool FullSha(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
        var evidence = ArgumentValue(args, "--evidence");
        var reference = ArgumentValue(args, "--active-ref");
        var parent = ArgumentValue(args, "--expected-parent");
        var contendersValue = ArgumentValue(args, "--contenders");
        var contenders = contendersValue?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (authorization.ExpectedReference != reference || authorization.ExpectedParent != parent ||
            !(authorization.ExpectedContenders ?? []).SequenceEqual(contenders ?? [], StringComparer.OrdinalIgnoreCase) || authorization.Evidence != evidence)
            return false;
        return action switch
        {
            ActiveProbeAction.NativeReview or ActiveProbeAction.GateCheck => !string.IsNullOrWhiteSpace(evidence) && reference is null && parent is null && contenders is null,
            ActiveProbeAction.LeaseCompareAndSwap => reference == config.LeaseReference &&
                FullSha(parent) &&
                !string.IsNullOrWhiteSpace(evidence) &&
                contenders is { Length: 2 } shas && shas.All(FullSha) && shas[0] != shas[1],
            ActiveProbeAction.WorkerNegativePermission => reference == config.ControlReference && parent is null && contenders is null && !string.IsNullOrWhiteSpace(evidence),
            _ => false
        };
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static System.Text.Json.JsonSerializerOptions CreateJsonOptions()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase));
        return options;
    }

    private static bool IsApprovedStageOneConfig(StageOneGitHubConfig config)
        => config.RepositoryId == 1463547883 && config.PullRequest == 1 &&
           config.HeadSha == "354099dbca836c3cf0e01b7b4bd637279ad80fa9" && config.TestMergeSha == "725dc8bbc799947dd733e10e76d23f67796c16c7" &&
           config.RequiredCheck == "sandbox-contract" && config.RequiredCheckAppId == 15368 && config.RulesetId == 27610516 &&
           config.ReviewerAppId == 5826689 && config.GateAppId == 5826708 && config.MergeAppId == 5826720 && config.WorkerAppId == 5826750 && config.OrchestratorAppId == 5827575 &&
           config.ControlReference == "refs/heads/agentrelay-control" && config.LeaseReference == "refs/heads/agentrelay-locks/v2_bootstrap_probe";
}
