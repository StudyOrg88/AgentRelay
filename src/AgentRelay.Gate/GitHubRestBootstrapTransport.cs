using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AgentRelay.Gate;

public sealed class GitHubRestBootstrapTransport : IGitHubBootstrapTransport
{
    private const string ApiBase = "https://api.github.com";
    private readonly HttpClient _client;
    private readonly StageOneGitHubConfig _config;
    private readonly IReadOnlyDictionary<long, string> _installationScopeTokens = new Dictionary<long, string>();
    private readonly ActiveProbeAuthorization? _authorization;
    private readonly bool _activeEnabled;

    public GitHubRestBootstrapTransport(HttpClient client, string token, StageOneGitHubConfig config, bool activeEnabled = false, ActiveProbeAuthorization? authorization = null)
    {
        _client = client;
        _client.BaseAddress ??= new Uri(ApiBase);
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        _client.DefaultRequestHeaders.UserAgent.ParseAdd("AgentRelay-Gate-bootstrap-probe");
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _config = config;
        _activeEnabled = activeEnabled;
        _authorization = authorization;
        if (activeEnabled && !IsValidAuthorization(authorization, config))
            throw new ArgumentException("Valid stage-two authorization is required for active transport.", nameof(authorization));
    }

    public GitHubRestBootstrapTransport(HttpClient client, StageOneGitHubCredentials credentials, StageOneGitHubConfig config)
        : this(client, credentials.ConfigurationToken, config)
    {
        _installationScopeTokens = credentials.InstallationScopeTokens;
    }

    public async Task<BootstrapEvidence?> ReadEvidenceAsync(BootstrapProbeRequest request, CancellationToken cancellationToken)
    {
        try { return await ReadEvidenceCoreAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (GitHubTransportException) { throw; }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { throw Failure(null); }
    }

    private async Task<BootstrapEvidence?> ReadEvidenceCoreAsync(BootstrapProbeRequest request, CancellationToken cancellationToken)
    {
        EnsureRepository(request.Repository);
        if (request.Base != "main" || !request.ExpectedChecks.SequenceEqual([_config.RequiredCheck], StringComparer.Ordinal))
            throw Failure(HttpStatusCode.Forbidden);

        var target = await DiscoverRequiredCheckTargetAsync(request.Base, cancellationToken).ConfigureAwait(false);
        Require(target.AdminsEnforced && !target.AllowForcePushes && !target.AllowDeletions);

        var ruleset = await GetJsonAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}/rulesets/{_config.RulesetId}", cancellationToken).ConfigureAwait(false);
        ValidateRuleset(ruleset);
        var rulesets = await GetJsonAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}/rulesets?per_page=100", cancellationToken).ConfigureAwait(false);
        var mergeQueueEnabled = rulesets.EnumerateArray().Any(ruleSet => ruleSet.GetProperty("rules").EnumerateArray().Any(rule => rule.GetProperty("type").GetString() == "merge_queue"));
        var installations = await GetJsonAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}/installations?per_page=100", cancellationToken).ConfigureAwait(false);
        await ValidateRoleInstallationsAsync(installations, cancellationToken).ConfigureAwait(false);

        return new BootstrapEvidence(
            PermissionsKnown: true,
            WorkerCanMerge: false,
            WorkerCanBypass: false,
            WorkerCanWriteControlRefs: false,
            ReviewerIdentity: _config.ReviewerAppId.ToString(),
            GateIdentity: _config.GateAppId.ToString(),
            MergeIdentity: _config.MergeAppId.ToString(),
            GateCheckApp: _config.GateAppId.ToString(),
            ExpectedChecks: [_config.RequiredCheck],
            MergeQueueEnabled: mergeQueueEnabled,
            LeaseCapabilityKnown: true,
            LeaseCreateUsesNonForceCas: true,
            LeaseCompetitionSingleWinner: true,
            GateTargetKnown: true,
            GateTargetCommit: target.EffectiveTarget,
            GateTargetsRequiredCommit: true,
            GateTargetAssociatedWithReviewedHead: true);
    }

    public async Task<ActiveProbeResult?> ExecuteActiveAsync(ActiveProbeRequest request, CancellationToken cancellationToken)
    {
        try
        {
            EnsureRepository(request.Repository);
            ValidateActiveAuthorization(request);
            EnsureActiveBinding(request);
            return request.Action switch
            {
                ActiveProbeAction.NativeReview => await PublishReviewAsync(request, cancellationToken).ConfigureAwait(false),
                ActiveProbeAction.GateCheck => await PublishCheckAsync(request, cancellationToken).ConfigureAwait(false),
                ActiveProbeAction.LeaseCompareAndSwap => await CompareAndSwapAsync(request, cancellationToken).ConfigureAwait(false),
                ActiveProbeAction.WorkerNegativePermission => await ReadWorkerPermissionsAsync(request, cancellationToken).ConfigureAwait(false),
                _ => throw Failure(HttpStatusCode.Forbidden)
            };
        }
        catch (GitHubTransportException) { throw; }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { throw Failure(null); }
    }

    private async Task<ActiveProbeResult> PublishReviewAsync(ActiveProbeRequest request, CancellationToken cancellationToken)
    {
        var evidenceDigest = ActiveProbeEvidence.Digest(request.Evidence!);
        var body = JsonSerializer.Serialize(new { commit_id = request.ExpectedHead, body = $"AgentRelay bootstrap probe evidence sha256:{evidenceDigest}", @event = "COMMENT" });
        using var response = await SendAsync(HttpMethod.Post, $"repos/{AllowlistedGitHubApi.SandboxRepository}/pulls/{_config.PullRequest}/reviews", body, cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessfulJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var item = document.RootElement;
        Require(StringComparer.OrdinalIgnoreCase.Equals(item.GetProperty("commit_id").GetString(), request.ExpectedHead));
        Require(item.GetProperty("performed_via_github_app").GetProperty("id").GetInt64() == _config.ReviewerAppId);
        return new ActiveProbeResult(RequireEvidence(item, "html_url", "id"), request.ExpectedHead, request.ExpectedHead, Evidence: evidenceDigest);
    }

    private async Task<ActiveProbeResult> PublishCheckAsync(ActiveProbeRequest request, CancellationToken cancellationToken)
    {
        var discovered = await DiscoverRequiredCheckTargetAsync("main", cancellationToken).ConfigureAwait(false);
        Require(StringComparer.OrdinalIgnoreCase.Equals(request.ExpectedHead, discovered.HeadSha) &&
            StringComparer.OrdinalIgnoreCase.Equals(request.ExpectedTargetCommit, discovered.EffectiveTarget) &&
            StringComparer.OrdinalIgnoreCase.Equals(_authorization?.ExpectedBaseSha, discovered.BaseSha));

        var current = await DiscoverRequiredCheckTargetAsync("main", cancellationToken).ConfigureAwait(false);
        Require(discovered == current && StringComparer.OrdinalIgnoreCase.Equals(request.ExpectedHead, current.HeadSha) &&
            StringComparer.OrdinalIgnoreCase.Equals(request.ExpectedTargetCommit, current.EffectiveTarget) &&
            StringComparer.OrdinalIgnoreCase.Equals(_authorization?.ExpectedBaseSha, current.BaseSha));

        var body = JsonSerializer.Serialize(new
        {
            name = "agentrelay-gate",
            head_sha = discovered.EffectiveTarget,
            status = "completed",
            conclusion = "success",
            output = new { title = "Sandbox bootstrap probe", summary = $"Evidence sha256:{ActiveProbeEvidence.Digest(request.Evidence!)}; reviewed_head: {request.ExpectedHead}" }
        });
        using var response = await SendAsync(HttpMethod.Post, $"repos/{AllowlistedGitHubApi.SandboxRepository}/check-runs", body, cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessfulJsonAsync(response, cancellationToken).ConfigureAwait(false);
        var item = document.RootElement;
        Require(StringComparer.OrdinalIgnoreCase.Equals(item.GetProperty("head_sha").GetString(), discovered.EffectiveTarget) &&
            item.GetProperty("app").GetProperty("id").GetInt64() == _config.GateAppId);
        return new ActiveProbeResult(RequireEvidence(item, "html_url", "id"), discovered.EffectiveTarget, request.ExpectedHead, Evidence: ActiveProbeEvidence.Digest(request.Evidence!));
    }

    private async Task<RequiredCheckTarget> DiscoverRequiredCheckTargetAsync(string baseRef, CancellationToken cancellationToken)
    {
        var repo = await GetJsonAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}", cancellationToken).ConfigureAwait(false);
        Require(repo.GetProperty("id").GetInt64() == _config.RepositoryId &&
            repo.GetProperty("full_name").GetString() == AllowlistedGitHubApi.SandboxRepository &&
            repo.GetProperty("default_branch").GetString() == baseRef);

        var pr = await GetJsonAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}/pulls/{_config.PullRequest}", cancellationToken).ConfigureAwait(false);
        var baseInfo = pr.GetProperty("base");
        var baseSha = baseInfo.GetProperty("sha").GetString();
        var headSha = pr.GetProperty("head").GetProperty("sha").GetString();
        var mergeSha = pr.GetProperty("merge_commit_sha").GetString();
        Require(pr.GetProperty("number").GetInt32() == _config.PullRequest && pr.GetProperty("state").GetString() == "open" &&
            baseInfo.GetProperty("ref").GetString() == baseRef && IsSha(baseSha) && IsSha(headSha) && IsSha(mergeSha) &&
            StringComparer.OrdinalIgnoreCase.Equals(headSha, _config.HeadSha) &&
            StringComparer.OrdinalIgnoreCase.Equals(mergeSha, _config.TestMergeSha));

        var protection = await GetJsonAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}/branches/{Uri.EscapeDataString(baseRef)}/protection", cancellationToken).ConfigureAwait(false);
        var requiredStatus = protection.GetProperty("required_status_checks");
        var required = requiredStatus.GetProperty("checks").EnumerateArray().Where(check =>
            StringComparer.Ordinal.Equals(check.GetProperty("context").GetString(), _config.RequiredCheck)).ToArray();
        Require(required.Length == 1 && required[0].GetProperty("app_id").GetInt64() == _config.RequiredCheckAppId);

        var strict = requiredStatus.GetProperty("strict").GetBoolean();
        var headHasRequiredCheck = await HasSuccessfulRequiredCheckAsync(headSha!, cancellationToken).ConfigureAwait(false);
        var mergeHasRequiredCheck = await HasSuccessfulRequiredCheckAsync(mergeSha!, cancellationToken).ConfigureAwait(false);
        var target = strict ? (mergeHasRequiredCheck ? mergeSha : null) : (headHasRequiredCheck ? headSha : null);
        Require(target is not null);

        var adminsEnforced = protection.GetProperty("enforce_admins").GetProperty("enabled").GetBoolean();
        var allowForcePushes = protection.GetProperty("allow_force_pushes").GetProperty("enabled").GetBoolean();
        var allowDeletions = protection.GetProperty("allow_deletions").GetProperty("enabled").GetBoolean();
        return new RequiredCheckTarget(baseRef, baseSha!, headSha!, mergeSha!, strict,
            _config.RequiredCheck, _config.RequiredCheckAppId, adminsEnforced, allowForcePushes, allowDeletions, target!);
    }

    private async Task<bool> HasSuccessfulRequiredCheckAsync(string sha, CancellationToken cancellationToken)
    {
        var checks = await GetJsonAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}/commits/{sha}/check-runs", cancellationToken).ConfigureAwait(false);
        var runs = checks.GetProperty("check_runs").EnumerateArray().ToArray();
        Require(checks.GetProperty("total_count").GetInt32() == runs.Length);
        return runs.Any(check =>
            StringComparer.Ordinal.Equals(check.GetProperty("name").GetString(), _config.RequiredCheck) &&
            StringComparer.OrdinalIgnoreCase.Equals(check.GetProperty("head_sha").GetString(), sha) &&
            check.GetProperty("status").GetString() == "completed" &&
            check.GetProperty("conclusion").GetString() == "success" &&
            check.GetProperty("app").GetProperty("id").GetInt64() == _config.RequiredCheckAppId);
    }

    private sealed record RequiredCheckTarget(string BaseRef, string BaseSha, string HeadSha, string TestMergeSha,
        bool Strict, string Context, long AppId, bool AdminsEnforced, bool AllowForcePushes, bool AllowDeletions, string EffectiveTarget);

    private async Task<ActiveProbeResult> CompareAndSwapAsync(ActiveProbeRequest request, CancellationToken cancellationToken)
    {
        if (request.Reference != _config.LeaseReference || string.IsNullOrWhiteSpace(request.Evidence) || !IsSha(request.ExpectedParent) || request.Contenders is not { Count: 2 } contenders || contenders.Any(item => !IsSha(item)) || StringComparer.OrdinalIgnoreCase.Equals(contenders[0], contenders[1]))
            throw Failure(HttpStatusCode.Forbidden);

        var reference = ToApiRef(request.Reference);
        var before = await GetOptionalJsonAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}/git/ref/{reference}", cancellationToken).ConfigureAwait(false);
        Require(before is not null && StringComparer.OrdinalIgnoreCase.Equals(before.Value.GetProperty("object").GetProperty("sha").GetString(), request.ExpectedParent));
        foreach (var contender in contenders)
        {
            var commit = await GetJsonAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}/git/commits/{contender}", cancellationToken).ConfigureAwait(false);
            var parents = commit.GetProperty("parents").EnumerateArray().ToArray();
            Require(parents.Length == 1 && StringComparer.OrdinalIgnoreCase.Equals(parents[0].GetProperty("sha").GetString(), request.ExpectedParent));
        }
        var outcomes = await Task.WhenAll(contenders.Select(contender =>
            CompareAndSwapContenderAsync(reference, contender, cancellationToken))).ConfigureAwait(false);
        Require(outcomes.Count(item => item.Won) == 1);
        return new ActiveProbeResult($"lease-cas:{string.Join(',', outcomes.Select(item => $"{item.Commit[..12]}={(item.Won ? "won" : "lost")}"))}", request.ExpectedTargetCommit, request.ExpectedHead,
            request.Reference, request.ExpectedParent, contenders, ActiveProbeEvidence.Digest(request.Evidence!));
    }

    private async Task<(string Commit, bool Won)> CompareAndSwapContenderAsync(string reference, string contender, CancellationToken cancellationToken)
    {
        var body = JsonSerializer.Serialize(new { sha = contender, force = false });
        using var response = await SendAsync(HttpMethod.Patch, $"repos/{AllowlistedGitHubApi.SandboxRepository}/git/refs/{reference}", body, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.UnprocessableEntity)
            return (contender, false);
        using var document = await ReadSuccessfulJsonAsync(response, cancellationToken).ConfigureAwait(false);
        Require(StringComparer.OrdinalIgnoreCase.Equals(document.RootElement.GetProperty("object").GetProperty("sha").GetString(), contender));
        return (contender, true);
    }

    private async Task<ActiveProbeResult> ReadWorkerPermissionsAsync(ActiveProbeRequest request, CancellationToken cancellationToken)
    {
        Require(request.Reference == _config.ControlReference && !string.IsNullOrWhiteSpace(request.Evidence));
        var installation = await GetJsonAsync("installation/repositories", cancellationToken).ConfigureAwait(false);
        var repositories = installation.GetProperty("repositories").EnumerateArray().ToArray();
        Require(repositories.Length == 1 && repositories[0].GetProperty("id").GetInt64() == _config.RepositoryId && repositories[0].GetProperty("full_name").GetString() == AllowlistedGitHubApi.SandboxRepository);
        var permissions = repositories[0].GetProperty("permissions");
        ValidateExactPermissions(permissions, new Dictionary<string, string>
        {
            ["metadata"] = "read",
            ["contents"] = "read",
            ["pull_requests"] = "read"
        });

        var pr = await GetJsonAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}/pulls/{_config.PullRequest}", cancellationToken).ConfigureAwait(false);
        Require(StringComparer.OrdinalIgnoreCase.Equals(pr.GetProperty("head").GetProperty("sha").GetString(), request.ExpectedHead));
        var ruleset = await GetJsonAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}/rulesets/{_config.RulesetId}", cancellationToken).ConfigureAwait(false);
        ValidateRuleset(ruleset);
        var bypassActors = ruleset.GetProperty("bypass_actors").EnumerateArray().ToArray();
        Require(!bypassActors.Any(actor => actor.GetProperty("actor_id").GetInt64() == _config.WorkerAppId));

        var reviewBody = JsonSerializer.Serialize(new { commit_id = request.ExpectedHead, body = $"AgentRelay worker permission probe sha256:{ActiveProbeEvidence.Digest(request.Evidence!)}" });
        await RequireReviewDeniedAsync($"repos/{AllowlistedGitHubApi.SandboxRepository}/pulls/{_config.PullRequest}/reviews", reviewBody, cancellationToken).ConfigureAwait(false);
        var deniedSha = new string('0', 40);
        var mergeBody = JsonSerializer.Serialize(new { sha = deniedSha, merge_method = "squash" });
        await RequireForbiddenAsync(HttpMethod.Put, $"repos/{AllowlistedGitHubApi.SandboxRepository}/pulls/{_config.PullRequest}/merge", mergeBody, cancellationToken).ConfigureAwait(false);
        var refBody = JsonSerializer.Serialize(new { sha = deniedSha, force = false });
        await RequireForbiddenAsync(HttpMethod.Patch, $"repos/{AllowlistedGitHubApi.SandboxRepository}/git/refs/{ToApiRef(_config.ControlReference)}", refBody, cancellationToken).ConfigureAwait(false);
        await RequireForbiddenAsync(HttpMethod.Patch, $"repos/{AllowlistedGitHubApi.SandboxRepository}/git/refs/{ToApiRef(_config.LeaseReference)}", refBody, cancellationToken).ConfigureAwait(false);
        return new ActiveProbeResult($"worker-permissions:{_config.WorkerAppId}:contents=read,pull_requests=read,review-write=403,merge-write=403,control-ref-write=403,lease-ref-write=403,bypass=not-listed-in-active-ruleset", request.ExpectedTargetCommit, request.ExpectedHead,
            request.Reference, Evidence: ActiveProbeEvidence.Digest(request.Evidence!));
    }

    private async Task RequireForbiddenAsync(HttpMethod method, string path, string body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(method, path, body, cancellationToken).ConfigureAwait(false);
        Require(response.StatusCode == HttpStatusCode.Forbidden);
    }

    private async Task RequireReviewDeniedAsync(string path, string body, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Forbidden) return;
        if (response.IsSuccessStatusCode)
        {
            using var created = await ReadSuccessfulJsonAsync(response, cancellationToken).ConfigureAwait(false);
            var reviewId = created.RootElement.GetProperty("id").ToString();
            using var cleanup = await SendAsync(HttpMethod.Delete,
                $"repos/{AllowlistedGitHubApi.SandboxRepository}/pulls/{_config.PullRequest}/reviews/{Uri.EscapeDataString(reviewId)}", null, cancellationToken).ConfigureAwait(false);
            if (!cleanup.IsSuccessStatusCode) throw Failure(cleanup.StatusCode);
            throw Failure(HttpStatusCode.Forbidden);
        }
        throw Failure(response.StatusCode);
    }

    private async Task<JsonElement> GetJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessfulJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement> GetJsonAsync(string path, string token, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, token, cancellationToken).ConfigureAwait(false);
        using var document = await ReadSuccessfulJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private async Task<JsonElement?> GetOptionalJsonAsync(string path, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        using var document = await ReadSuccessfulJsonAsync(response, cancellationToken).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, CancellationToken cancellationToken)
        => await SendAsync(method, path, body, null, cancellationToken).ConfigureAwait(false);

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, string? token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try { return await _client.SendAsync(request, cancellationToken).ConfigureAwait(false); }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { throw Failure(null); }
    }

    private static async Task<JsonDocument> ReadSuccessfulJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode) throw Failure(response.StatusCode);
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException) { throw Failure(response.StatusCode); }
    }

    private void ValidateRuleset(JsonElement ruleset)
    {
        Require(ruleset.GetProperty("id").GetInt64() == _config.RulesetId && ruleset.GetProperty("enforcement").GetString() == "active");
        var includes = ruleset.GetProperty("conditions").GetProperty("ref_name").GetProperty("include").EnumerateArray().Select(item => item.GetString()).ToArray();
        Require(includes.Contains(_config.ControlReference, StringComparer.Ordinal) && includes.Contains("refs/heads/agentrelay-locks/**", StringComparer.Ordinal));
        var bypass = ruleset.GetProperty("bypass_actors").EnumerateArray().ToArray();
        Require(bypass.Length == 1 && bypass[0].GetProperty("actor_id").GetInt64() == _config.OrchestratorAppId && bypass[0].GetProperty("actor_type").GetString() == "Integration");
        var rules = ruleset.GetProperty("rules").EnumerateArray().Select(item => item.GetProperty("type").GetString()).ToHashSet(StringComparer.Ordinal);
        Require(new[] { "creation", "update", "deletion", "non_fast_forward" }.All(rules.Contains));
    }

    private async Task ValidateRoleInstallationsAsync(JsonElement installations, CancellationToken cancellationToken)
    {
        var items = installations.GetProperty("installations").EnumerateArray().ToArray();
        var expected = new Dictionary<long, IReadOnlyDictionary<string, string>>
        {
            [_config.ReviewerAppId] = new Dictionary<string, string> { ["metadata"] = "read", ["pull_requests"] = "write" },
            [_config.GateAppId] = new Dictionary<string, string> { ["metadata"] = "read", ["contents"] = "read", ["pull_requests"] = "read", ["checks"] = "write" },
            [_config.MergeAppId] = new Dictionary<string, string> { ["metadata"] = "read", ["contents"] = "write", ["pull_requests"] = "write" },
            [_config.WorkerAppId] = new Dictionary<string, string> { ["metadata"] = "read", ["contents"] = "read", ["pull_requests"] = "read" },
            [_config.OrchestratorAppId] = new Dictionary<string, string> { ["metadata"] = "read", ["contents"] = "write" }
        };
        Require(items.Length == expected.Count);
        foreach (var role in expected)
        {
            var matches = items.Where(item => item.GetProperty("app_id").GetInt64() == role.Key).ToArray();
            Require(matches.Length == 1 && matches[0].GetProperty("repository_selection").GetString() == "selected");
            var installationId = matches[0].GetProperty("id").GetInt64();
            Require(installationId > 0);
            ValidateExactPermissions(matches[0].GetProperty("permissions"), role.Value);
            await ValidateInstallationScopeAsync(role.Key, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void ValidateExactPermissions(JsonElement permissions, IReadOnlyDictionary<string, string> expected)
    {
        var entries = permissions.EnumerateObject().ToArray();
        Require(entries.Length == expected.Count && entries.All(entry =>
            expected.TryGetValue(entry.Name, out var permission) && StringComparer.Ordinal.Equals(entry.Value.GetString(), permission)));
    }

    private async Task ValidateInstallationScopeAsync(long appId, CancellationToken cancellationToken)
    {
        if (_installationScopeTokens.Count != 5 ||
            !_installationScopeTokens.TryGetValue(appId, out var installationToken) ||
            string.IsNullOrWhiteSpace(installationToken))
            throw Failure(HttpStatusCode.Forbidden);
        var scope = await GetJsonAsync("installation/repositories?per_page=100", installationToken, cancellationToken).ConfigureAwait(false);
        var repositories = scope.GetProperty("repositories").EnumerateArray().ToArray();
        Require(scope.GetProperty("total_count").GetInt32() == 1 && repositories.Length == 1 &&
            repositories[0].GetProperty("id").GetInt64() == _config.RepositoryId &&
            repositories[0].GetProperty("full_name").GetString() == AllowlistedGitHubApi.SandboxRepository);
    }

    private void EnsureActiveBinding(ActiveProbeRequest request)
    {
        Require(request.PullRequest == _config.PullRequest && StringComparer.OrdinalIgnoreCase.Equals(request.ExpectedHead, _config.HeadSha));
        Require(request.Action == ActiveProbeAction.GateCheck
            ? StringComparer.OrdinalIgnoreCase.Equals(request.ExpectedTargetCommit, _config.TestMergeSha) || StringComparer.OrdinalIgnoreCase.Equals(request.ExpectedTargetCommit, _config.HeadSha)
            : StringComparer.OrdinalIgnoreCase.Equals(request.ExpectedTargetCommit, _config.HeadSha));
        if (request.Action == ActiveProbeAction.NativeReview || request.Action == ActiveProbeAction.GateCheck)
            Require(!string.IsNullOrWhiteSpace(request.Evidence));
    }

    private void ValidateActiveAuthorization(ActiveProbeRequest request)
    {
        var authorization = _authorization;
        Require(_activeEnabled);
        var expectedIdentity = request.Action switch
        {
            ActiveProbeAction.NativeReview => authorization?.ReviewerIdentity,
            ActiveProbeAction.GateCheck => authorization?.GateIdentity,
            ActiveProbeAction.LeaseCompareAndSwap => authorization?.OrchestratorIdentity,
            ActiveProbeAction.WorkerNegativePermission => authorization?.WorkerIdentity,
            _ => null
        };
        Require(authorization is { AllowActiveProbe: true } && authorization.ExpiresAt > DateTimeOffset.UtcNow &&
            authorization.Repository == AllowlistedGitHubApi.SandboxRepository && authorization.PullRequest == request.PullRequest &&
            authorization.TaskId == request.TaskId && authorization.AllowedActions is { Count: 1 } && authorization.AllowedActions.Contains(request.Action) &&
            StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedHead, request.ExpectedHead) &&
            StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedTargetCommit, request.ExpectedTargetCommit) &&
            StringComparer.Ordinal.Equals(authorization.ExpectedReference, request.Reference) &&
            StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedParent, request.ExpectedParent) &&
            (authorization.ExpectedContenders ?? []).SequenceEqual(request.Contenders ?? [], StringComparer.OrdinalIgnoreCase) &&
            StringComparer.Ordinal.Equals(authorization.Evidence, request.Evidence) &&
            StringComparer.Ordinal.Equals(expectedIdentity, request.Identity));
    }

    private static bool IsValidAuthorization(ActiveProbeAuthorization? authorization, StageOneGitHubConfig config)
        => authorization is { AllowActiveProbe: true, AllowedActions.Count: 1 } && authorization.ExpiresAt > DateTimeOffset.UtcNow &&
           authorization.Repository == AllowlistedGitHubApi.SandboxRepository && authorization.TaskId == "v2_bootstrap_probe" &&
           authorization.PullRequest == config.PullRequest && StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedHead, config.HeadSha) &&
           (StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedTargetCommit, config.HeadSha) || StringComparer.OrdinalIgnoreCase.Equals(authorization.ExpectedTargetCommit, config.TestMergeSha)) &&
           authorization.AllowedActions.All(action => action switch
           {
               ActiveProbeAction.NativeReview => authorization.ReviewerIdentity == config.ReviewerAppId.ToString() && authorization.ExpectedReference is null && authorization.ExpectedParent is null && authorization.ExpectedContenders is null && !string.IsNullOrWhiteSpace(authorization.Evidence),
               ActiveProbeAction.GateCheck => authorization.GateIdentity == config.GateAppId.ToString() && IsSha(authorization.ExpectedBaseSha) && authorization.ExpectedReference is null && authorization.ExpectedParent is null && authorization.ExpectedContenders is null && !string.IsNullOrWhiteSpace(authorization.Evidence),
               ActiveProbeAction.LeaseCompareAndSwap => authorization.OrchestratorIdentity == config.OrchestratorAppId.ToString() && authorization.ExpectedReference == config.LeaseReference && IsSha(authorization.ExpectedParent) && authorization.ExpectedContenders is { Count: 2 } && authorization.ExpectedContenders.All(IsSha) && authorization.ExpectedContenders[0] != authorization.ExpectedContenders[1] && !string.IsNullOrWhiteSpace(authorization.Evidence),
               ActiveProbeAction.WorkerNegativePermission => authorization.WorkerIdentity == config.WorkerAppId.ToString() && authorization.ExpectedReference == config.ControlReference && authorization.ExpectedParent is null && authorization.ExpectedContenders is null && !string.IsNullOrWhiteSpace(authorization.Evidence),
               _ => false
           });

    private static void EnsureRepository(string repository)
    {
        if (!StringComparer.Ordinal.Equals(repository, AllowlistedGitHubApi.SandboxRepository)) throw Failure(HttpStatusCode.Forbidden);
    }

    private static string ToApiRef(string reference)
    {
        const string prefix = "refs/heads/";
        Require(reference.StartsWith(prefix, StringComparison.Ordinal));
        return "heads/" + reference[prefix.Length..];
    }

    private static string RequireEvidence(JsonElement item, string urlProperty, string idProperty)
    {
        var url = item.GetProperty(urlProperty).GetString();
        var id = item.GetProperty(idProperty).ToString();
        Require(!string.IsNullOrWhiteSpace(url) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host == "github.com" && !string.IsNullOrWhiteSpace(id));
        return url!;
    }

    private static bool IsSha(string? value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);
    private static void Require(bool condition) { if (!condition) throw Failure(HttpStatusCode.Forbidden); }
    private static GitHubTransportException Failure(HttpStatusCode? status) => new("GitHub transport failed.", status is null ? null : (int)status.Value);
}
