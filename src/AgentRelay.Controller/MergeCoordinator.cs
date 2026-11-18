using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentRelay.Controller;

public sealed record VerifiedMergeGates(string PullRequestHeadSha, string PullRequestBaseSha);

public sealed record MergeGateResult(bool AcceptanceValid, string Reason, VerifiedMergeGates? Gates = null);

public sealed class MergeCoordinator(GitAcceptanceVerifier verifier)
{
    public MergeGateResult Evaluate(string repositoryRoot, GitHubStateSnapshot snapshot, string taskId) =>
        verifier.Verify(repositoryRoot, snapshot, taskId);
}

public sealed class GitAcceptanceVerifier
{
    private const string SchemaPath = "docs/acceptance-schema-v1.json";
    private const string PolicyPath = "scripts/acceptance-policy.ps1";

    public MergeGateResult Verify(string repositoryRoot, GitHubStateSnapshot snapshot, string taskId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (string.IsNullOrWhiteSpace(repositoryRoot) || string.IsNullOrWhiteSpace(taskId) ||
            !Regex.IsMatch(taskId, @"\A[A-Za-z0-9][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant))
            return Blocked("Repository or task identity is invalid.");

        try
        {
            var issueIdentity = ParseIssueIdentity(snapshot.IssueUrl);
            var origin = RunGit(repositoryRoot, "remote", "get-url", "origin").Output;
            if (!StringComparer.OrdinalIgnoreCase.Equals(issueIdentity, ParseGitHubIdentity(origin)))
                return Blocked("Target repository origin does not match the Issue repository.");
            if (!StringComparer.Ordinal.Equals(snapshot.PullRequestBaseRef, snapshot.RepositoryDefaultBranch))
                return Blocked("Pull request base is not the repository default branch.");
            if (!IsSha(snapshot.PullRequestBaseSha) || !IsSha(snapshot.CurrentHeadSha))
                return Blocked("Pull request head or base SHA is missing or invalid.");

            var baseSha = snapshot.PullRequestBaseSha!;
            var headSha = snapshot.CurrentHeadSha!;
            RequireCommit(repositoryRoot, baseSha);
            RequireCommit(repositoryRoot, headSha);
            var acceptancePath = $"acceptance/{taskId}.v1.json";
            var schemaJson = Show(repositoryRoot, baseSha, SchemaPath);
            var policy = Show(repositoryRoot, baseSha, PolicyPath);
            if (string.IsNullOrWhiteSpace(policy) ||
                !policy.Contains("function Test-AgentRelayReviewValidity", StringComparison.Ordinal) ||
                !policy.Contains("function Test-AgentRelayMergeBlocked", StringComparison.Ordinal))
                return Blocked("Trusted Acceptance policy is missing or incompatible at PR base.");
            if (RunGit(repositoryRoot, "diff", "--quiet", baseSha, headSha, "--", SchemaPath, PolicyPath).ExitCode == 1)
                return Blocked("Pull request changes trusted Acceptance schema or policy.");

            using var schemaDoc = JsonDocument.Parse(schemaJson);
            var schemaRoot = schemaDoc.RootElement;
            if (!schemaRoot.TryGetProperty("properties", out var schemaProperties) ||
                !schemaProperties.TryGetProperty("schema_version", out var versionSchema) ||
                !versionSchema.TryGetProperty("const", out var version) || version.GetString() != "acceptance/v1")
                return Blocked("PR base does not contain the supported Acceptance v1 schema.");

            var recordJson = Show(repositoryRoot, headSha, acceptancePath);
            using var recordDoc = JsonDocument.Parse(recordJson);
            var record = recordDoc.RootElement;
            if (!MatchesAcceptanceSchema(schemaRoot, record) ||
                RequiredString(record, "schema_version") != "acceptance/v1" ||
                RequiredString(record, "task_id") != taskId)
                return Blocked("Acceptance record identity or base binding is invalid.");

            var reviewedCodeSha = RequiredString(record, "reviewed_code_commit");
            var acceptanceBaseSha = RequiredString(record, "acceptance_base_commit");
            var declaredParent = RequiredString(record, "record_commit_parent");
            if (!IsSha(reviewedCodeSha) || !IsSha(acceptanceBaseSha) || !IsSha(declaredParent))
                return Blocked("Acceptance record contains an invalid commit reference.");
            RequireCommit(repositoryRoot, reviewedCodeSha);
            if (RunGit(repositoryRoot, "merge-base", "--is-ancestor", reviewedCodeSha, headSha).ExitCode != 0)
                return Blocked("Reviewed code commit is not an ancestor of the current PR head.");

            var reviewedChanges = RunGit(repositoryRoot, "diff", "--name-status", "--diff-filter=ACMRTD", reviewedCodeSha, headSha).Output;
            foreach (var line in reviewedChanges.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var columns = line.Split('\t');
                if (columns.Length != 2 || columns[1] != acceptancePath || columns[0].StartsWith('D'))
                    return Blocked("Changes after the reviewed code commit are not limited to the Acceptance record.");
            }

            var recordCommit = RunGit(repositoryRoot, "log", "-1", "--format=%H", headSha, "--", acceptancePath).Output;
            RequireCommit(repositoryRoot, recordCommit);
            var actualParent = RunGit(repositoryRoot, "rev-parse", "--verify", $"{recordCommit}^1").Output;
            if (!StringComparer.Ordinal.Equals(actualParent, declaredParent))
                return Blocked("Acceptance record parent does not match its actual first parent.");

            if (!record.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() == 0)
                return Blocked("Acceptance record has no valid items.");
            foreach (var item in items.EnumerateArray())
            {
                if (RequiredString(item, "id").Length == 0 || RequiredString(item, "owner_task").Length == 0 ||
                    !item.TryGetProperty("merge_blocking", out var blocking) || blocking.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                    !item.TryGetProperty("status", out var statusValue) || statusValue.ValueKind != JsonValueKind.String)
                    return Blocked("Acceptance item is missing required ownership or status fields.");
                if (RequiredString(item, "owner_task") == taskId && blocking.GetBoolean() && RequiredString(item, "status") != "passed")
                    return Blocked("A merge-blocking Acceptance item has not passed.");
            }

            if (snapshot.Review is not { Result: "PASS" } review ||
                !StringComparer.Ordinal.Equals(review.ReviewedHeadSha, headSha) ||
                !StringComparer.Ordinal.Equals(review.ReviewedCodeCommit, reviewedCodeSha) ||
                !StringComparer.Ordinal.Equals(review.ReviewedBaseSha, baseSha) ||
                !StringComparer.Ordinal.Equals(review.AcceptanceBaseCommit, acceptanceBaseSha) ||
                !StringComparer.Ordinal.Equals(review.RecordCommitParent, declaredParent) ||
                !StringComparer.OrdinalIgnoreCase.Equals(review.Reviewer, review.CommentAuthor) ||
                string.IsNullOrWhiteSpace(review.SessionId) || review.ReviewedAt == default ||
                review.Findings.Count != 0 || review.Evidence.Count == 0 ||
                !IsSha(review.ReviewedHeadSha) || !IsSha(review.ReviewedCodeCommit) || !IsSha(review.ReviewedBaseSha) ||
                !IsSha(review.AcceptanceBaseCommit) || !IsSha(review.RecordCommitParent))
                return Blocked("Reviewer PASS is missing or is not bound to the current PR head and base.");
            if (RunGit(repositoryRoot, "merge-base", "--is-ancestor", review.ReviewedHeadSha, headSha).ExitCode != 0)
                return Blocked("Reviewed code commit is not an ancestor of the current PR head.");

            return new(true, "Trusted Acceptance v1 and head/base-bound review are valid.", new(headSha, baseSha));
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or JsonException or ArgumentException)
        {
            return Blocked($"Acceptance verification failed closed: {exception.Message}");
        }
    }

    private static string ParseGitHubIdentity(string value)
    {
        var match = Regex.Match(value ?? "", @"\A(?:https://github\.com/|ssh://git@github\.com/|git@github\.com:)(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9_.-]+?)(?:\.git)?/?\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("Repository identity must be a GitHub.com HTTPS or SSH URL.");
        return $"github.com/{match.Groups["owner"].Value}/{match.Groups["repo"].Value}";
    }

    private static string ParseIssueIdentity(string value)
    {
        var match = Regex.Match(value ?? "", @"\Ahttps://github\.com/(?<owner>[A-Za-z0-9-]+)/(?<repo>[A-Za-z0-9_.-]+)/issues/[1-9][0-9]*\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        if (!match.Success) throw new InvalidOperationException("Issue identity must be a canonical GitHub.com URL.");
        return $"github.com/{match.Groups["owner"].Value}/{match.Groups["repo"].Value}";
    }

    private static bool MatchesAcceptanceSchema(JsonElement schema, JsonElement record)
    {
        if (!schema.TryGetProperty("required", out var required) || required.ValueKind != JsonValueKind.Array ||
            required.EnumerateArray().Any(field => field.ValueKind != JsonValueKind.String || !record.TryGetProperty(field.GetString()!, out _)))
            return false;
        if (!schema.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("additionalProperties", out var additionalProperties) || additionalProperties.ValueKind != JsonValueKind.False)
            return false;
        var allowed = properties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        if (record.EnumerateObject().Any(property => !allowed.Contains(property.Name))) return false;

        foreach (var property in properties.EnumerateObject())
        {
            if (!record.TryGetProperty(property.Name, out var value) || !property.Value.TryGetProperty("type", out var type)) continue;
            if (type.GetString() == "string" && value.ValueKind != JsonValueKind.String) return false;
            if (type.GetString() == "array" && value.ValueKind != JsonValueKind.Array) return false;
        }

        var itemSchema = properties.GetProperty("items").GetProperty("items");
        var itemProperties = itemSchema.GetProperty("properties");
        var itemAllowed = itemProperties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var item in record.GetProperty("items").EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                itemSchema.GetProperty("required").EnumerateArray().Any(field => !item.TryGetProperty(field.GetString()!, out _)) ||
                item.EnumerateObject().Any(property => !itemAllowed.Contains(property.Name))) return false;
            if (item.GetProperty("id").ValueKind != JsonValueKind.String || item.GetProperty("owner_task").ValueKind != JsonValueKind.String ||
                item.GetProperty("status").ValueKind != JsonValueKind.String || item.GetProperty("merge_blocking").ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                item.GetProperty("accepted_evidence").ValueKind != JsonValueKind.Array || item.GetProperty("rejected_evidence").ValueKind != JsonValueKind.Array ||
                item.GetProperty("evidence_type").ValueKind != JsonValueKind.String) return false;
            if (item.GetProperty("accepted_evidence").EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String) ||
                item.GetProperty("rejected_evidence").EnumerateArray().Any(value => value.ValueKind != JsonValueKind.String)) return false;
            if (!new[] { "pending", "passed", "failed", "not_applicable" }.Contains(item.GetProperty("status").GetString(), StringComparer.Ordinal) ||
                !new[] { "command", "artifact", "manual", "ci" }.Contains(item.GetProperty("evidence_type").GetString(), StringComparer.Ordinal)) return false;
        }
        return true;
    }

    private static bool IsSha(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-f]{40}\z", RegexOptions.CultureInvariant);

    private static string RequiredString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!
            : throw new JsonException($"Required string '{name}' is missing.");

    private static void RequireCommit(string repositoryRoot, string sha)
    {
        if (!IsSha(sha) || RunGit(repositoryRoot, "cat-file", "-e", $"{sha}^{{commit}}").ExitCode != 0)
            throw new InvalidOperationException("Required full commit object is missing.");
    }

    private static string Show(string repositoryRoot, string commit, string path) =>
        RunGit(repositoryRoot, "show", $"{commit}:{path}").Output;

    private static ProcessResult RunGit(string repositoryRoot, params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git")
            {
                WorkingDirectory = repositoryRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            }
        };
        foreach (var argument in arguments) process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode is not (0 or 1)) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? "Git object verification failed." : error.Trim());
        return new(process.ExitCode, output.Trim());
    }

    private static MergeGateResult Blocked(string reason) => new(false, reason);
    private sealed record ProcessResult(int ExitCode, string Output);
}
