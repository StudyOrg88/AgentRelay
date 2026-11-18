using AgentRelay.Controller;
using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace AgentRelay.Controller.Tests;

public sealed class MergeCoordinatorTests
{
    [Theory]
    [InlineData("https://github.com/acme/repo.git", "https://github.com/acme/repo/issues/7", true)]
    [InlineData("git@github.com:Acme/Repo.git", "https://github.com/acme/repo/issues/7", true)]
    [InlineData("https://github.com/other/repo.git", "https://github.com/acme/repo/issues/7", false)]
    [InlineData("https://github.example.com/acme/repo.git", "https://github.com/acme/repo/issues/7", false)]
    public void Target_repository_origin_must_match_the_github_com_issue(
        string origin, string issueUrl, bool expected)
    {
        using var fixture = new AcceptanceRepository(origin);

        var result = fixture.Evaluate(issueUrl);

        Assert.Equal(expected, result.AcceptanceValid);
    }

    [Fact]
    public void Pull_request_must_target_repository_default_branch()
    {
        using var fixture = new AcceptanceRepository("https://github.com/acme/repo.git");

        var result = fixture.Evaluate(baseRef: "release/1");

        Assert.False(result.AcceptanceValid);
    }

    [Theory]
    [InlineData("docs/acceptance-schema-v1.json")]
    [InlineData("scripts/acceptance-policy.ps1")]
    public void Pull_request_cannot_change_its_trusted_acceptance_policy(string changedPath)
    {
        using var fixture = new AcceptanceRepository("https://github.com/acme/repo.git", changeTrustedFileAtHead: changedPath);

        var result = fixture.Evaluate();

        Assert.False(result.AcceptanceValid);
    }

    [Fact]
    public void Missing_base_object_fails_closed()
    {
        using var fixture = new AcceptanceRepository("https://github.com/acme/repo.git");

        var result = fixture.Evaluate(baseSha: new string('f', 40));

        Assert.False(result.AcceptanceValid);
    }

    [Fact]
    public void Review_must_bind_both_current_head_and_base()
    {
        using var fixture = new AcceptanceRepository("https://github.com/acme/repo.git");

        var wrongHead = fixture.Evaluate(reviewedHeadSha: new string('d', 40));
        var wrongBase = fixture.Evaluate(reviewedBaseSha: new string('c', 40));

        Assert.False(wrongHead.AcceptanceValid);
        Assert.False(wrongBase.AcceptanceValid);
    }

    [Theory]
    [InlineData("reviewer-bot", "worker-bot", false, true)]
    [InlineData("reviewer-bot", "reviewer-bot", true, true)]
    [InlineData("reviewer-bot", "reviewer-bot", false, false)]
    public void Pass_requires_matching_reviewer_identity_empty_findings_and_evidence(
        string reviewer, string author, bool hasEvidence, bool findingsEmpty)
    {
        using var fixture = new AcceptanceRepository("https://github.com/acme/repo.git");

        var result = fixture.Evaluate(reviewer: reviewer, commentAuthor: author, findingsEmpty: findingsEmpty,
            hasEvidence: hasEvidence);

        Assert.Equal(StringComparer.OrdinalIgnoreCase.Equals(reviewer, author) && findingsEmpty && hasEvidence,
            result.AcceptanceValid);
    }

    [Fact]
    public void Valid_default_base_record_and_head_base_bound_review_are_accepted()
    {
        using var fixture = new AcceptanceRepository("ssh://git@github.com/acme/repo.git");

        var result = fixture.Evaluate();

        Assert.True(result.AcceptanceValid);
        Assert.Equal(fixture.HeadSha, result.Gates!.PullRequestHeadSha);
        Assert.Equal(fixture.BaseSha, result.Gates.PullRequestBaseSha);
    }

    [Fact]
    public void Current_task_pending_merge_blocking_item_is_rejected()
    {
        using var fixture = new AcceptanceRepository("https://github.com/acme/repo.git", itemStatus: "pending");

        Assert.False(fixture.Evaluate().AcceptanceValid);
    }

    [Theory]
    [InlineData("failed", "another_task", true)]
    [InlineData("failed", "trusted_acceptance", false)]
    public void Only_current_task_merge_blocking_items_block(string status, string ownerTask, bool mergeBlocking)
    {
        using var fixture = new AcceptanceRepository("https://github.com/acme/repo.git", itemStatus: status,
            itemOwner: ownerTask, mergeBlocking: mergeBlocking);

        Assert.Equal(!(mergeBlocking && ownerTask == "trusted_acceptance"), fixture.Evaluate().AcceptanceValid);
    }

    private sealed class AcceptanceRepository : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "agentrelay-acceptance-" + Guid.NewGuid().ToString("N"));
        private readonly string _schema;
        private readonly string _policy;
        private string _reviewedCodeSha = "";
        private string _recordSha = "";
        private readonly string? _changedPath;
        private readonly string _itemStatus;
        private readonly string _itemOwner;
        private readonly bool _mergeBlocking;

        public AcceptanceRepository(string origin, string? changeTrustedFileAtHead = null, string itemStatus = "passed",
            string itemOwner = "trusted_acceptance", bool mergeBlocking = true)
        {
            Directory.CreateDirectory(_root);
            Run("init");
            Run("config", "user.email", "worker@example.test");
            Run("config", "user.name", "Worker Test");
            Run("remote", "add", "origin", origin);
            _schema = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "docs", "acceptance-schema-v1.json"));
            _policy = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "scripts", "acceptance-policy.ps1"));
            _changedPath = changeTrustedFileAtHead;
            _itemStatus = itemStatus;
            _itemOwner = itemOwner;
            _mergeBlocking = mergeBlocking;
            Write("docs/acceptance-schema-v1.json", _schema);
            Write("scripts/acceptance-policy.ps1", _policy);
            Run("add", ".");
            Run("commit", "-m", "trusted base");
            BaseSha = Output("rev-parse", "HEAD");

            Write("src/code.cs", "class Code {}\n");
            if (_changedPath is not null) Write(_changedPath, "changed trusted content\n");
            Run("add", ".");
            Run("commit", "-m", "reviewed code");
            _reviewedCodeSha = Output("rev-parse", "HEAD");
            WriteAcceptanceRecord();
            Run("add", ".");
            Run("commit", "-m", "acceptance evidence");
            _recordSha = Output("rev-parse", "HEAD");
            HeadSha = _recordSha;
        }

        public string BaseSha { get; }
        public string HeadSha { get; }

        public MergeGateResult Evaluate(string issueUrl = "https://github.com/acme/repo/issues/7",
            string? baseSha = null, string baseRef = "master", string? reviewedHeadSha = null,
            string? reviewedBaseSha = null, string reviewer = "reviewer-bot", string? commentAuthor = null,
            bool findingsEmpty = true, bool hasEvidence = true)
        {
            var snapshot = new GitHubStateSnapshot(issueUrl, ["task:review"], true, 9,
                "https://github.com/acme/repo/pull/9", HeadSha, baseSha ?? BaseSha, baseRef, "master", false, null)
            {
                Review = new MergeReviewEvidence("PASS", reviewedHeadSha ?? HeadSha, _reviewedCodeSha,
                    reviewedBaseSha ?? BaseSha, BaseSha, _reviewedCodeSha, reviewer, "session-123",
                    DateTimeOffset.Parse("2026-11-18T10:00:00+08:00"), findingsEmpty ? [] : ["finding"],
                    hasEvidence ? ["targeted tests passed"] : [], commentAuthor ?? reviewer)
            };
            return new MergeCoordinator(new GitAcceptanceVerifier()).Evaluate(_root, snapshot, "trusted_acceptance");
        }

        private void WriteAcceptanceRecord()
        {
            var record = new
            {
                schema_version = "acceptance/v1",
                task_id = "trusted_acceptance",
                reviewed_code_commit = _reviewedCodeSha,
                acceptance_base_commit = BaseSha,
                record_commit_parent = _reviewedCodeSha,
                items = new[]
                {
                    new
                    {
                        id = "trusted-base",
                        status = _itemStatus,
                        merge_blocking = _mergeBlocking,
                        evidence_type = "command",
                        accepted_evidence = new[] { "verified from PR base" },
                        rejected_evidence = new[] { "unverifiable evidence" },
                        owner_task = _itemOwner
                    }
                }
            };
            Write("acceptance/trusted_acceptance.v1.json", JsonSerializer.Serialize(record));
        }

        private void Write(string relativePath, string contents)
        {
            var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        private string Output(params string[] args) => Run(args);

        private string Run(params string[] args)
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("git")
                {
                    WorkingDirectory = _root,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false
                }
            };
            foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
            return output.Trim();
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "docs", "acceptance-schema-v1.json")))
                directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate repository fixtures.");
        }

        public void Dispose()
        {
            if (!Directory.Exists(_root)) return;
            foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(path, FileAttributes.Normal);
            foreach (var path in Directory.EnumerateDirectories(_root, "*", SearchOption.AllDirectories).OrderByDescending(path => path.Length))
                File.SetAttributes(path, FileAttributes.Directory);
            Directory.Delete(_root, recursive: true);
        }
    }
}
