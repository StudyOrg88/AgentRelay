using AgentRelay.Controller;
using System.Text.Json;
using Xunit;

namespace AgentRelay.Controller.Tests;

public sealed class GitHubStateReaderTests
{
    [Fact]
    public async Task Issue_without_pull_request_reads_successfully_with_empty_pr_metadata()
    {
        var reader = new GitHubStateReader(new FakeGitHubClient(IssueOnlyJson));

        var snapshot = await reader.ReadAsync("https://github.com/acme/repo/issues/7");

        Assert.Null(snapshot.PullRequestBaseSha);
        Assert.Null(snapshot.PullRequestBaseRef);
        Assert.Null(snapshot.RepositoryDefaultBranch);
        Assert.Null(snapshot.MergeCommitSha);
        Assert.Equal(AgentRelay.Core.TaskAction.Worker, AgentRelay.Core.TaskLifecycle.Decide(snapshot.ToTaskSnapshot()).Action);
    }

    [Fact]
    public async Task Pull_request_metadata_exposes_base_and_default_branch()
    {
        var reader = new GitHubStateReader(new FakeGitHubClient(PullRequestJson));

        var snapshot = await reader.ReadAsync("https://github.com/acme/repo/issues/7");

        Assert.Equal(new string('b', 40), snapshot.PullRequestBaseSha);
        Assert.Equal("master", snapshot.PullRequestBaseRef);
        Assert.Equal("master", snapshot.RepositoryDefaultBranch);
        Assert.False(snapshot.Merged);
    }

    [Fact]
    public async Task Merged_pull_request_exposes_valid_merge_commit_sha()
    {
        var mergeSha = new string('c', 40);
        var snapshot = await new GitHubStateReader(new FakeGitHubClient(
            PrJson(new string('b', 40), "master", "master", true, mergeSha)))
            .ReadAsync("https://github.com/acme/repo/issues/7");

        Assert.True(snapshot.Merged);
        Assert.Equal(mergeSha, snapshot.MergeCommitSha);
    }

    [Theory]
    [InlineData("short", "master", "master", false, null)]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "bad..ref", "master", false, null)]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "master", "", false, null)]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "master", "master", true, null)]
    [InlineData("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "master", "master", true, "short")]
    public async Task Invalid_or_unverifiable_pr_metadata_fails_closed(
        string baseSha, string baseRef, string defaultBranch, bool merged, string? mergeSha)
    {
        var json = PrJson(baseSha, baseRef, defaultBranch, merged, mergeSha);
        var reader = new GitHubStateReader(new FakeGitHubClient(json));

        await Assert.ThrowsAsync<GitHubStateUnavailableException>(() =>
            reader.ReadAsync("https://github.com/acme/repo/issues/7"));
    }

    [Fact]
    public async Task Production_gh_client_reads_issue_and_timeline_without_requesting_pr_metadata_when_no_pr_exists()
    {
        var process = new FakeGhProcessInvoker(new Dictionary<string, string>
        {
            ["repos/acme/repo/issues/7"] = """{"number":7,"html_url":"https://github.com/acme/repo/issues/7","body":"depends_on: none","labels":[{"name":"task:planned"}]}""",
            ["repos/acme/repo/issues/7/timeline?per_page=100"] = "[]"
        });

        var json = await new GhGitHubClient(processInvoker: process).ReadIssueStateAsync("https://github.com/acme/repo/issues/7");
        var snapshot = await new GitHubStateReader(new FakeGitHubClient(json)).ReadAsync("https://github.com/acme/repo/issues/7");

        Assert.Null(snapshot.PullRequestEvidenceId);
        Assert.Equal(AgentRelay.Core.TaskAction.Worker, AgentRelay.Core.TaskLifecycle.Decide(snapshot.ToTaskSnapshot()).Action);
        Assert.Equal(2, process.Routes.Count);
        Assert.DoesNotContain(process.Routes, route => route.Contains("/pulls/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Production_gh_client_reads_linked_pr_and_repository_default_branch()
    {
        var head = new string('a', 40);
        var baseSha = new string('b', 40);
        var process = new FakeGhProcessInvoker(new Dictionary<string, string>
        {
            ["repos/acme/repo/issues/7"] = """{"number":7,"html_url":"https://github.com/acme/repo/issues/7","body":"depends_on: []","labels":[{"name":"task:review"}]}""",
            ["repos/acme/repo/issues/7/timeline?per_page=100"] = """[{"event":"cross-referenced","source":{"issue":{"html_url":"https://github.com/acme/repo/pull/9","pull_request":{"url":"https://api.github.com/repos/acme/repo/pulls/9"}}}}]""",
            ["repos/acme/repo/pulls/9"] = $$"""{"number":9,"html_url":"https://github.com/acme/repo/pull/9","state":"open","draft":false,"merged":false,"head":{"sha":"{{head}}"},"base":{"sha":"{{baseSha}}","ref":"master"},"merge_commit_sha":null}""",
            ["repos/acme/repo"] = """{"default_branch":"master"}"""
        });

        var json = await new GhGitHubClient(processInvoker: process).ReadIssueStateAsync("https://github.com/acme/repo/issues/7");
        var snapshot = await new GitHubStateReader(new FakeGitHubClient(json)).ReadAsync("https://github.com/acme/repo/issues/7");

        Assert.Equal(baseSha, snapshot.PullRequestBaseSha);
        Assert.Equal("master", snapshot.PullRequestBaseRef);
        Assert.Equal("master", snapshot.RepositoryDefaultBranch);
        Assert.Contains("repos/acme/repo/pulls/9", process.Routes);
        Assert.Contains("repos/acme/repo", process.Routes);
    }

    private static string IssueOnlyJson => JsonSerializer.Serialize(new
    {
        repository = "acme/repo",
        issue = new { number = 7, url = "https://github.com/acme/repo/issues/7", labels = new[] { "task:planned" }, dependencies = Array.Empty<object>() },
        pull_request = (object?)null
    });

    private static string PullRequestJson => PrJson(new string('b', 40), "master", "master", false, null);

    private static string PrJson(string baseSha, string baseRef, string defaultBranch, bool merged, string? mergeSha) =>
        JsonSerializer.Serialize(new
        {
            repository = "acme/repo",
            issue = new { number = 7, url = "https://github.com/acme/repo/issues/7", labels = new[] { "task:review" }, dependencies = Array.Empty<object>() },
            pull_request = new
            {
                number = 9,
                url = "https://github.com/acme/repo/pull/9",
                associated_issue_url = "https://github.com/acme/repo/issues/7",
                state = "OPEN",
                draft = false,
                merged,
                head_sha = new string('a', 40),
                base_sha = baseSha,
                base_ref = baseRef,
                default_branch = defaultBranch,
                merge_commit_sha = mergeSha
            }
        });

    private sealed class FakeGitHubClient(string json) : IGitHubClient
    {
        public Task<string> ReadIssueStateAsync(string canonicalIssueUrl, CancellationToken cancellationToken = default) =>
            Task.FromResult(json);
    }

    private sealed class FakeGhProcessInvoker(IReadOnlyDictionary<string, string> responses) : IProcessInvoker
    {
        public List<string> Routes { get; } = [];

        public Task<ProcessResult> InvokeAsync(ProcessInvocation invocation, CancellationToken cancellationToken)
        {
            var route = invocation.Arguments[1];
            Routes.Add(route);
            return responses.TryGetValue(route, out var response)
                ? Task.FromResult(new ProcessResult(0, response, ""))
                : Task.FromResult(new ProcessResult(1, "", "unexpected API route"));
        }
    }
}
