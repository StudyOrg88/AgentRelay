using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentRelay.Core;

namespace AgentRelay.Controller;

public interface IGitHubClient
{
    Task<string> ReadIssueStateAsync(string canonicalIssueUrl, CancellationToken cancellationToken = default);
}

public sealed class GitHubClientException(string message) : Exception(message);
public sealed class GitHubStateUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record GitHubStateSnapshot(
    string IssueUrl,
    IReadOnlyList<string> Labels,
    bool DependenciesReady,
    int? PullRequestNumber,
    string? PullRequestEvidenceId,
    string? CurrentHeadSha,
    string? PullRequestBaseSha,
    string? PullRequestBaseRef,
    string? RepositoryDefaultBranch,
    bool Merged,
    string? MergeCommitSha)
{
    public MergeReviewEvidence? Review { get; init; }

    public TaskSnapshot ToTaskSnapshot() => new(IssueUrl, Labels, DependenciesReady, PullRequestEvidenceId)
    {
        PullRequestHeadSha = CurrentHeadSha,
        PullRequestBaseSha = PullRequestBaseSha,
        ReviewedHeadSha = Review?.ReviewedHeadSha,
        ReviewedBaseSha = Review?.ReviewedBaseSha,
        ReviewResult = Review?.Result
    };
}

public sealed record MergeReviewEvidence(
    string Result,
    string ReviewedHeadSha,
    string ReviewedCodeCommit,
    string ReviewedBaseSha,
    string AcceptanceBaseCommit,
    string RecordCommitParent,
    string Reviewer,
    string SessionId,
    DateTimeOffset ReviewedAt,
    IReadOnlyList<string> Findings,
    IReadOnlyList<string> Evidence,
    string CommentAuthor);

public sealed class GitHubStateReader(IGitHubClient client)
{
    private static readonly Regex IssueUrlPattern = new(
        @"\Ahttps://github\.com/(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9_.-]+)/issues/(?<number>[1-9][0-9]*)\z",
        RegexOptions.CultureInvariant);

    public async Task<GitHubStateSnapshot> ReadAsync(string issueUrl, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        var match = IssueUrlPattern.Match(issueUrl ?? "");
        if (!match.Success) throw new ArgumentException("Issue URL must be canonical.", nameof(issueUrl));
        var canonical = $"https://github.com/{match.Groups["owner"].Value}/{match.Groups["repo"].Value}/issues/{match.Groups["number"].Value}";
        string json;
        try
        {
            json = await client.ReadIssueStateAsync(canonical, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { throw new GitHubStateUnavailableException("GitHub state is unavailable.", exception); }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var repository = RequiredString(root, "repository");
            var expectedRepository = $"{match.Groups["owner"].Value}/{match.Groups["repo"].Value}";
            if (!StringComparer.OrdinalIgnoreCase.Equals(repository, expectedRepository))
                throw new FormatException("Repository identity mismatch.");

            var issue = RequiredObject(root, "issue");
            if (RequiredInt(issue, "number").ToString(System.Globalization.CultureInfo.InvariantCulture) != match.Groups["number"].Value ||
                RequiredString(issue, "url") != canonical)
                throw new FormatException("Canonical Issue identity mismatch.");
            var labels = RequiredArray(issue, "labels").EnumerateArray()
                .Select(label => label.GetString() ?? throw new FormatException("Invalid label."))
                .ToArray();
            var dependenciesReady = RequiredArray(issue, "dependencies").EnumerateArray()
                .All(dependency => RequiredString(dependency, "state") is "CLOSED" or "MERGED");

            if (!root.TryGetProperty("pull_request", out var pr) || pr.ValueKind == JsonValueKind.Null)
                return new(canonical, labels, dependenciesReady, null, null, null, null, null, null, false, null);

            var number = RequiredInt(pr, "number");
            var prUrl = RequiredString(pr, "url");
            var prMatch = Regex.Match(prUrl,
                @"\Ahttps://github\.com/(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9_.-]+)/pull/(?<number>[1-9][0-9]*)\z",
                RegexOptions.CultureInvariant);
            if (!prMatch.Success ||
                !StringComparer.OrdinalIgnoreCase.Equals(prMatch.Groups["owner"].Value, match.Groups["owner"].Value) ||
                !StringComparer.OrdinalIgnoreCase.Equals(prMatch.Groups["repo"].Value, match.Groups["repo"].Value) ||
                prMatch.Groups["number"].Value != number.ToString(System.Globalization.CultureInfo.InvariantCulture) ||
                RequiredString(pr, "associated_issue_url") != canonical)
                throw new FormatException("Pull request identity or Issue association is invalid.");

            var merged = RequiredBoolean(pr, "merged");
            var headSha = RequiredFullSha(pr, "head_sha");
            var baseSha = RequiredFullSha(pr, "base_sha");
            var baseRef = RequiredSafeGitRef(pr, "base_ref");
            var defaultBranch = RequiredSafeGitRef(pr, "default_branch");
            var mergeSha = merged ? RequiredFullSha(pr, "merge_commit_sha") : OptionalFullSha(pr, "merge_commit_sha");
            var review = ParseReview(pr);
            return new GitHubStateSnapshot(canonical, labels, dependenciesReady, number, prUrl, headSha, baseSha, baseRef,
                defaultBranch, merged, mergeSha) { Review = review };
        }
        catch (Exception exception) when (exception is not GitHubStateUnavailableException)
        {
            throw new GitHubStateUnavailableException("GitHub state is unavailable or invalid.", exception);
        }
    }

    private static JsonElement RequiredObject(JsonElement parent, string name) => Required(parent, name, JsonValueKind.Object);
    private static JsonElement RequiredArray(JsonElement parent, string name) => Required(parent, name, JsonValueKind.Array);
    private static string RequiredString(JsonElement parent, string name) => Required(parent, name, JsonValueKind.String).GetString()!;
    private static int RequiredInt(JsonElement parent, string name) => Required(parent, name, JsonValueKind.Number).GetInt32();
    private static bool RequiredBoolean(JsonElement parent, string name) => Required(parent, name, JsonValueKind.True, JsonValueKind.False).GetBoolean();
    private static string RequiredFullSha(JsonElement parent, string name) => ValidateSha(RequiredString(parent, name), name);
    private static string? OptionalFullSha(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? ValidateSha(value.GetString()!, name)
            : parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Null
                ? null
                : throw new FormatException($"Missing or invalid {name}.");

    private static string ValidateSha(string? value, string name) => value is not null &&
        Regex.IsMatch(value, @"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant)
            ? value
            : throw new FormatException($"Missing or invalid {name}.");

    private static string RequiredSafeGitRef(JsonElement parent, string name)
    {
        var value = RequiredString(parent, name);
        if (value.Length == 0 || value[0] is '-' or '/' || value[^1] is '/' or '.' || value is "@" ||
            value.Contains("..", StringComparison.Ordinal) || value.Contains("//", StringComparison.Ordinal) ||
            value.Contains("@{", StringComparison.Ordinal) ||
            value.Split('/').Any(part => part.Length == 0 || part[0] == '.' || part.EndsWith(".lock", StringComparison.OrdinalIgnoreCase)) ||
            Regex.IsMatch(value, @"[\x00-\x20\x7f~^:?*\[\\]", RegexOptions.CultureInvariant))
            throw new FormatException($"Missing or invalid {name}.");
        return value;
    }

    private static JsonElement Required(JsonElement parent, string name, params JsonValueKind[] kinds) =>
        parent.TryGetProperty(name, out var value) && kinds.Contains(value.ValueKind)
            ? value
            : throw new FormatException($"Missing or invalid {name}.");

    private static MergeReviewEvidence? ParseReview(JsonElement root)
    {
        if (!root.TryGetProperty("review", out var review) || review.ValueKind == JsonValueKind.Null) return null;
        var result = RequiredString(review, "result");
        var reviewedHead = RequiredFullSha(review, "reviewed_head_sha");
        var reviewedCode = RequiredFullSha(review, "reviewed_code_commit");
        var reviewedBase = RequiredFullSha(review, "reviewed_base_sha");
        var acceptanceBase = RequiredFullSha(review, "acceptance_base_commit");
        var recordParent = RequiredFullSha(review, "record_commit_parent");
        var reviewer = RequiredString(review, "reviewer");
        var sessionId = RequiredString(review, "session_id");
        var reviewedAtText = RequiredString(review, "reviewed_at");
        var reviewedAt = DateTimeOffset.TryParse(reviewedAtText, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsedAt)
            ? parsedAt
            : throw new FormatException("Invalid reviewer timestamp.");
        var findings = RequiredArray(review, "findings").EnumerateArray()
            .Select(item => item.GetString() ?? throw new FormatException("Invalid reviewer finding." )).ToArray();
        var evidence = RequiredArray(review, "evidence").EnumerateArray()
            .Select(item => item.GetString() ?? throw new FormatException("Invalid reviewer evidence.")).ToArray();
        var commentAuthor = RequiredString(review, "comment_author");
        if (result is not ("PASS" or "CHANGES_REQUESTED")) throw new FormatException("Invalid reviewer result.");
        return new(result, reviewedHead, reviewedCode, reviewedBase, acceptanceBase, recordParent, reviewer,
            sessionId, reviewedAt, findings, evidence, commentAuthor);
    }
}

public sealed class GhGitHubClient : IGitHubClient
{
    private readonly IProcessInvoker _processInvoker;
    private readonly string _ghExecutable;
    private readonly TimeSpan _timeout;

    public GhGitHubClient(string ghExecutable = "gh", TimeSpan? timeout = null, IProcessInvoker? processInvoker = null)
    {
        _ghExecutable = string.IsNullOrWhiteSpace(ghExecutable) ? throw new ArgumentException("Executable is required.", nameof(ghExecutable)) : ghExecutable;
        _timeout = timeout ?? TimeSpan.FromMinutes(1);
        if (_timeout <= TimeSpan.Zero || _timeout > TimeSpan.FromMinutes(10))
            throw new ArgumentOutOfRangeException(nameof(timeout));
        _processInvoker = processInvoker ?? new DeviceTransport.SystemProcessInvoker();
    }

    public async Task<string> ReadIssueStateAsync(string canonicalIssueUrl, CancellationToken cancellationToken = default)
    {
        var match = Regex.Match(canonicalIssueUrl ?? "",
            @"\Ahttps://github\.com/(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9_.-]+)/issues/(?<number>[1-9][0-9]*)\z",
            RegexOptions.CultureInvariant);
        if (!match.Success) throw new GitHubClientException("Canonical GitHub.com Issue URL required.");
        var owner = match.Groups["owner"].Value;
        var repository = match.Groups["repo"].Value;
        var number = match.Groups["number"].Value;
        var issueJson = await ReadApiAsync($"repos/{owner}/{repository}/issues/{number}", cancellationToken).ConfigureAwait(false);
        var timelineJson = await ReadApiAsync($"repos/{owner}/{repository}/issues/{number}/timeline?per_page=100", cancellationToken, paginate: true).ConfigureAwait(false);
        var issueUrl = $"https://github.com/{owner}/{repository}/issues/{number}";
        var dependencies = await ReadDependenciesAsync(owner, repository, number, issueJson, cancellationToken).ConfigureAwait(false);
        var prUrl = FindAssociatedPullRequest(timelineJson, owner, repository);
        object? pullRequest = null;
        if (prUrl is not null)
        {
            var prNumber = Regex.Match(prUrl, @"/pull/(?<number>[1-9][0-9]*)\z").Groups["number"].Value;
            using var prDoc = JsonDocument.Parse(await ReadApiAsync($"repos/{owner}/{repository}/pulls/{prNumber}", cancellationToken).ConfigureAwait(false));
            using var repoDoc = JsonDocument.Parse(await ReadApiAsync($"repos/{owner}/{repository}", cancellationToken).ConfigureAwait(false));
            var pr = prDoc.RootElement;
            var baseInfo = RequiredObject(pr, "base");
            var headInfo = RequiredObject(pr, "head");
            var pullNumber = RequiredInt(pr, "number");
            var reviewJson = await ReadApiAsync($"repos/{owner}/{repository}/issues/{pullNumber}/comments", cancellationToken, paginate: true).ConfigureAwait(false);
            var review = FindReview(reviewJson);
            pullRequest = new
            {
                number = pullNumber,
                url = RequiredString(pr, "html_url"),
                associated_issue_url = issueUrl,
                state = RequiredString(pr, "state").ToUpperInvariant(),
                draft = RequiredBoolean(pr, "draft"),
                merged = RequiredBoolean(pr, "merged"),
                head_sha = RequiredString(headInfo, "sha"),
                base_sha = RequiredString(baseInfo, "sha"),
                base_ref = RequiredString(baseInfo, "ref"),
                default_branch = RequiredString(repoDoc.RootElement, "default_branch"),
                merge_commit_sha = OptionalString(pr, "merge_commit_sha"),
                review
            };
        }

        using var issueDoc = JsonDocument.Parse(issueJson);
        var issue = issueDoc.RootElement;
        var labels = RequiredArray(issue, "labels").EnumerateArray()
            .Select(label => RequiredString(label, "name")).ToArray();
        var normalized = new
        {
            repository = $"{owner}/{repository}",
            issue = new
            {
                number = RequiredInt(issue, "number"),
                url = RequiredString(issue, "html_url"),
                labels,
                dependencies
            },
            pull_request = pullRequest
        };
        return JsonSerializer.Serialize(normalized);
    }

    private async Task<string> ReadApiAsync(string route, CancellationToken cancellationToken, bool paginate = false)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_timeout);
        ProcessResult result;
        try
        {
            var arguments = paginate ? new[] { "api", "--paginate", "--slurp", route } : ["api", route];
            result = await _processInvoker.InvokeAsync(new ProcessInvocation(_ghExecutable,
                arguments, "", _timeout), timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) { throw new GitHubClientException($"GitHub API request failed: {exception.GetType().Name}."); }
        if (result.ExitCode != 0) throw new GitHubClientException("GitHub API request failed.");
        return result.StandardOutput;
    }

    private async Task<object[]> ReadDependenciesAsync(string owner, string repository, string number, string issueJson,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(issueJson);
        var body = OptionalString(document.RootElement, "body") ?? "";
        var match = Regex.Match(body, @"(?im)^\s*depends_on\s*:\s*(?<value>.*?)\s*$", RegexOptions.CultureInvariant);
        if (!match.Success) throw new GitHubClientException("Issue depends_on field is missing.");
        var value = match.Groups["value"].Value.Trim();
        if (value.Equals("none", StringComparison.OrdinalIgnoreCase) || value == "[]") return [];
        var ids = Regex.Matches(value, @"#(?<number>[1-9][0-9]*)").Select(item => item.Groups["number"].Value).ToArray();
        if (ids.Length == 0 || Regex.Replace(value, @"#([1-9][0-9]*)|[\[\],\s]", "").Length != 0 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
            throw new GitHubClientException("Issue depends_on field is malformed.");
        var states = new List<object>();
        foreach (var id in ids)
        {
            if (id == number) throw new GitHubClientException("Issue cannot depend on itself.");
            using var dependencyDoc = JsonDocument.Parse(await ReadApiAsync($"repos/{owner}/{repository}/issues/{id}", cancellationToken).ConfigureAwait(false));
            var dependency = dependencyDoc.RootElement;
            var dependencyRepo = RequiredString(dependency, "repository_url");
            if (!dependencyRepo.EndsWith($"/repos/{owner}/{repository}", StringComparison.OrdinalIgnoreCase))
                throw new GitHubClientException("Dependency belongs to another repository.");
            states.Add(new { state = RequiredString(dependency, "state").ToUpperInvariant() });
        }
        return states.ToArray();
    }

    private static object? FindReview(string commentsJson)
    {
        using var document = JsonDocument.Parse(commentsJson);
        object? latest = null;
        foreach (var page in document.RootElement.EnumerateArray())
        {
            if (page.ValueKind == JsonValueKind.Array)
            {
                foreach (var comment in page.EnumerateArray()) latest = ParseReviewComment(comment) ?? latest;
            }
            else latest = ParseReviewComment(page) ?? latest;
        }
        return latest;
    }

    private static object? ParseReviewComment(JsonElement comment)
    {
        if (OptionalString(comment, "body") is not { } body || !body.Contains("agentrelay_review:", StringComparison.Ordinal)) return null;
        var markers = Regex.Matches(body, @"(?m)^agentrelay_review:\s*$", RegexOptions.CultureInvariant);
        var fence = Regex.Match(body, @"(?ms)^```yaml[ \t]*\r?\nagentrelay_review:[ \t]*\r?\n(?<block>.*?)\r?\n```[ \t]*");
        if (markers.Count != 1 || !fence.Success) return InvalidReview();

        var scalarFields = new Dictionary<string, string>(StringComparer.Ordinal);
        var evidence = new List<string>();
        var findingsHaveItems = false;
        string? activeList = null;
        foreach (var line in fence.Groups["block"].Value.Split("\n", StringSplitOptions.None))
        {
            var normalizedLine = line.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(normalizedLine)) continue;
            var field = Regex.Match(normalizedLine, @"\A  (?<key>[a-z_]+):(?:[ \t]*(?<value>.*))?\z", RegexOptions.CultureInvariant);
            if (field.Success)
            {
                var key = field.Groups["key"].Value;
                if (!ReviewFields.Contains(key) || scalarFields.ContainsKey(key)) return InvalidReview();
                var value = field.Groups["value"].Success ? field.Groups["value"].Value.Trim() : "";
                scalarFields.Add(key, value.Trim('"', '\''));
                activeList = key is "findings" or "evidence" ? key : null;
                continue;
            }

            if (normalizedLine.StartsWith("    - ", StringComparison.Ordinal) && activeList is "findings" or "evidence")
            {
                var item = normalizedLine[6..].Trim();
                if (item.Length == 0) return InvalidReview();
                if (activeList == "evidence") evidence.Add(item.Trim('"', '\''));
                else findingsHaveItems = true;
                continue;
            }
            if (normalizedLine.StartsWith("      ", StringComparison.Ordinal) && activeList is "findings" or "evidence") continue;
            return InvalidReview();
        }

        if (ReviewFields.Any(key => !scalarFields.ContainsKey(key))) return InvalidReview();
        var result = scalarFields["result"];
        if (result is not ("PASS" or "CHANGES_REQUESTED")) return InvalidReview();
        var findingsEmpty = scalarFields["findings"] == "[]";
        if (findingsEmpty && findingsHaveItems) return InvalidReview();
        if (!findingsEmpty && scalarFields["findings"].Length != 0) return InvalidReview();
        if (!new[] { "reviewed_head_sha", "reviewed_code_commit", "reviewed_base_sha", "acceptance_base_commit", "record_commit_parent" }
            .All(key => Regex.IsMatch(scalarFields[key], @"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant))) return InvalidReview();

        var author = comment.TryGetProperty("user", out var user) && user.ValueKind == JsonValueKind.Object
            ? OptionalString(user, "login") : null;
        if (author is null || string.IsNullOrWhiteSpace(scalarFields["reviewer"]) ||
            string.IsNullOrWhiteSpace(scalarFields["session_id"]) ||
            !DateTimeOffset.TryParse(scalarFields["reviewed_at"], System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out _)) return InvalidReview();
        return new
        {
            result,
            reviewed_head_sha = scalarFields["reviewed_head_sha"],
            reviewed_code_commit = scalarFields["reviewed_code_commit"],
            reviewed_base_sha = scalarFields["reviewed_base_sha"],
            acceptance_base_commit = scalarFields["acceptance_base_commit"],
            record_commit_parent = scalarFields["record_commit_parent"],
            reviewer = scalarFields["reviewer"],
            session_id = scalarFields["session_id"],
            reviewed_at = scalarFields["reviewed_at"],
            findings = findingsEmpty ? Array.Empty<string>() : new[] { "structured findings present" },
            evidence = evidence.ToArray(),
            comment_author = author
        };
    }

    private static readonly HashSet<string> ReviewFields = new(StringComparer.Ordinal)
    {
        "result", "reviewed_head_sha", "reviewed_code_commit", "reviewed_base_sha", "acceptance_base_commit",
        "record_commit_parent", "reviewer", "session_id", "reviewed_at", "findings", "evidence"
    };

    private static object InvalidReview() => new
    {
        result = "INVALID", reviewed_head_sha = "", reviewed_code_commit = "", reviewed_base_sha = "",
        acceptance_base_commit = "", record_commit_parent = "", reviewer = "", session_id = "", reviewed_at = "",
        findings = Array.Empty<string>(), evidence = Array.Empty<string>(), comment_author = ""
    };

    private static string? FindAssociatedPullRequest(string timelineJson, string owner, string repository)
    {
        using var document = JsonDocument.Parse(timelineJson);
        var root = document.RootElement;
        var items = root.EnumerateArray().SelectMany(page => page.ValueKind == JsonValueKind.Array
            ? page.EnumerateArray().Select(item => item.Clone())
            : [page.Clone()]);
        foreach (var item in items)
        {
            if (OptionalString(item, "event") != "cross-referenced" || !item.TryGetProperty("source", out var source) ||
                !source.TryGetProperty("issue", out var sourceIssue) || !sourceIssue.TryGetProperty("pull_request", out var pr)) continue;
            var url = OptionalString(sourceIssue, "html_url");
            var match = Regex.Match(url ?? "", @"\Ahttps://github\.com/(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9_.-]+)/pull/(?<number>[1-9][0-9]*)\z");
            if (match.Success && StringComparer.OrdinalIgnoreCase.Equals(match.Groups["owner"].Value, owner) &&
                StringComparer.OrdinalIgnoreCase.Equals(match.Groups["repo"].Value, repository)) return url;
        }
        return null;
    }

    private static JsonElement RequiredObject(JsonElement parent, string name) => Required(parent, name, JsonValueKind.Object);
    private static JsonElement RequiredArray(JsonElement parent, string name) => Required(parent, name, JsonValueKind.Array);
    private static string RequiredString(JsonElement parent, string name) => Required(parent, name, JsonValueKind.String).GetString()!;
    private static string? OptionalString(JsonElement parent, string name) =>
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() :
        parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Null ? null :
        throw new FormatException($"Missing or invalid {name}.");
    private static int RequiredInt(JsonElement parent, string name) => Required(parent, name, JsonValueKind.Number).GetInt32();
    private static bool RequiredBoolean(JsonElement parent, string name) => Required(parent, name, JsonValueKind.True, JsonValueKind.False).GetBoolean();
    private static JsonElement Required(JsonElement parent, string name, params JsonValueKind[] kinds) =>
        parent.TryGetProperty(name, out var value) && kinds.Contains(value.ValueKind) ? value : throw new FormatException($"Missing or invalid {name}.");
}
