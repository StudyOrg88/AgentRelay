using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentRelay.Gate;

namespace AgentRelay.Gate.Tests;

public sealed class GitHubRestTransportTests
{
    private const string Repository = "StudyOrg88/AgentRelay-v2-sandbox";
    private const string Head = "354099dbca836c3cf0e01b7b4bd637279ad80fa9";
    private const string TestMerge = "725dc8bbc799947dd733e10e76d23f67796c16c7";

    [Fact]
    public void DefaultProgramDependenciesDoNotReadLiveCredentials()
    {
        var reads = new List<string>();

        var api = Program.CreateGitHubApi([], name => { reads.Add(name); return "secret"; });

        Assert.IsType<FailClosedGitHubApi>(api);
        Assert.Empty(reads);
    }

    [Fact]
    public void LiveTransportRequiresExplicitSwitchAndStageOneConfigBeforeCredentialRead()
    {
        var reads = new List<string>();

        var api = Program.CreateGitHubApi(["--live-sandbox", "--stage-one-config", "missing.json"], name => { reads.Add(name); return "secret"; });

        Assert.IsType<FailClosedGitHubApi>(api);
        Assert.Empty(reads);
    }

    [Fact]
    public void LiveActiveTransportDoesNotReadRoleTokenUntilStageTwoBindingIsValid()
    {
        var configPath = Path.GetTempFileName();
        var authPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(configPath, JsonSerializer.Serialize(StageOneConfig()));
            File.WriteAllText(authPath, JsonSerializer.Serialize(new ActiveProbeAuthorization(Repository, 1, Head, DateTimeOffset.UtcNow.AddMinutes(5), false, "v2_bootstrap_probe", [ActiveProbeAction.NativeReview], "5826689", "5826708", "5826720", "5826750", "5827575", Head)));
            var reads = new List<string>();
            string[] args = ["--live-sandbox", "--stage-one-config", configPath, "--active", "--authorization-file", authPath,
                "--active-action", "native-review", "--active-identity", "5826689", "--test-task", "v2_bootstrap_probe", "--test-pr", "1",
                "--expected-head", Head, "--active-target", Head];

            var api = Program.CreateGitHubApi(args, name => { reads.Add(name); return "secret"; });

            Assert.IsType<FailClosedGitHubApi>(api);
            Assert.Empty(reads);
        }
        finally
        {
            File.Delete(configPath);
            File.Delete(authPath);
        }
    }

    [Fact]
    public void LiveActiveTransportReadsOnlyTheAuthorizedActionCredential()
    {
        var configPath = Path.GetTempFileName();
        var authPath = Path.GetTempFileName();
        try
        {
            File.WriteAllText(configPath, JsonSerializer.Serialize(StageOneConfig()));
            File.WriteAllText(authPath, JsonSerializer.Serialize(Authorization(ActiveProbeAction.NativeReview)));
            var reads = new List<string>();
            var args = new[] { "--live-sandbox", "--stage-one-config", configPath, "--active", "--authorization-file", authPath,
                "--active-action", "native-review", "--active-identity", "5826689", "--test-task", "v2_bootstrap_probe", "--test-pr", "1",
                "--expected-head", Head, "--active-target", Head, "--evidence", "review probe" };

            var api = Program.CreateGitHubApi(args, name => { reads.Add(name); return "ephemeral"; });

            Assert.IsType<AllowlistedGitHubApi>(api);
            Assert.Equal(new[] { "AGENTRELAY_TOKEN_REVIEWER" }, reads);
        }
        finally
        {
            File.Delete(configPath);
            File.Delete(authPath);
        }
    }

    [Fact]
    public async Task RestTransportCannotWriteWithoutBoundStageTwoAuthorization()
    {
        var handler = new ScriptedHandler(new Dictionary<string, HttpResponseMessage>());
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "active-token", StageOneConfig());

        await Assert.ThrowsAsync<GitHubTransportException>(() => transport.ExecuteActiveAsync(ActiveRequest(ActiveProbeAction.NativeReview) with { Evidence = "probe" }, CancellationToken.None));

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task ReadOnlyVerifiesExactStageOneFactsAndSources()
    {
        var handler = new ScriptedHandler(ReadOnlyResponses());
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "read-token", StageOneConfig());

        var evidence = await transport.ReadEvidenceAsync(ReadRequest(), CancellationToken.None);

        Assert.NotNull(evidence);
        Assert.Equal(new[] { "sandbox-contract" }, evidence.ExpectedChecks);
        Assert.Equal(TestMerge, evidence.GateTargetCommit);
        Assert.All(handler.Requests, request => Assert.Equal("Bearer", request.Authorization?.Scheme));
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task ReadOnlyFailsClosedOnForbiddenEmptyOrMismatchedFacts()
    {
        var responses = ReadOnlyResponses();
        responses["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/commits/725dc8bbc799947dd733e10e76d23f67796c16c7/check-runs"] =
            Response(HttpStatusCode.OK, "{\"total_count\":1,\"check_runs\":[{\"name\":\"sandbox-contract\",\"conclusion\":\"success\",\"app\":{\"id\":999}}]}");
        var transport = new GitHubRestBootstrapTransport(new HttpClient(new ScriptedHandler(responses)), "read-token", StageOneConfig());

        var error = await Assert.ThrowsAsync<GitHubTransportException>(() => transport.ReadEvidenceAsync(ReadRequest(), CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
        Assert.Equal("GitHub transport failed.", error.Message);
    }

    [Theory]
    [InlineData(ActiveProbeAction.NativeReview, "POST", "/repos/StudyOrg88/AgentRelay-v2-sandbox/pulls/1/reviews")]
    [InlineData(ActiveProbeAction.GateCheck, "POST", "/repos/StudyOrg88/AgentRelay-v2-sandbox/check-runs")]
    public async Task ActiveReviewAndGateUseOnlyNativeBoundEndpoints(ActiveProbeAction action, string method, string path)
    {
        var handler = new ScriptedHandler(new Dictionary<string, HttpResponseMessage>
        {
            [$"{method} {path}"] = action == ActiveProbeAction.NativeReview
                ? Response(HttpStatusCode.OK, "{\"id\":42,\"html_url\":\"https://github.com/review/42\",\"commit_id\":\"" + Head + "\",\"performed_via_github_app\":{\"id\":5826689}}")
                : Response(HttpStatusCode.Created, "{\"id\":43,\"html_url\":\"https://github.com/check/43\",\"head_sha\":\"" + TestMerge + "\",\"app\":{\"id\":5826708}}")
        });
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "active-token", StageOneConfig(), true, Authorization(action));
        var request = ActiveRequest(action) with { Evidence = "probe-evidence" };

        var result = await transport.ExecuteActiveAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Single(handler.Requests);
        Assert.Equal(path, handler.Requests[0].Path);
        Assert.DoesNotContain("merge", handler.Requests[0].Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LeaseCasUsesFixedRefExpectedParentTwoContendersAndForceFalse()
    {
        var contenderOne = new string('a', 40);
        var contenderTwo = new string('b', 40);
        var handler = new ScriptedHandler(new Dictionary<string, HttpResponseMessage>
        {
            ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/git/ref/heads/agentrelay-locks/v2_bootstrap_probe"] = Response(HttpStatusCode.OK, "{\"object\":{\"sha\":\"" + Head + "\"}}"),
            ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/git/commits/" + contenderOne] = Response(HttpStatusCode.OK, "{\"parents\":[{\"sha\":\"" + Head + "\"}]}"),
            ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/git/commits/" + contenderTwo] = Response(HttpStatusCode.OK, "{\"parents\":[{\"sha\":\"" + Head + "\"}]}"),
            ["PATCH /repos/StudyOrg88/AgentRelay-v2-sandbox/git/refs/heads/agentrelay-locks/v2_bootstrap_probe#1"] = Response(HttpStatusCode.OK, "{\"object\":{\"sha\":\"" + contenderOne + "\"}}"),
            ["PATCH /repos/StudyOrg88/AgentRelay-v2-sandbox/git/refs/heads/agentrelay-locks/v2_bootstrap_probe#2"] = Response(HttpStatusCode.UnprocessableEntity, "{\"message\":\"not fast forward\"}")
        });
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "orchestrator-token", StageOneConfig(), true, Authorization(ActiveProbeAction.LeaseCompareAndSwap));
        var request = ActiveRequest(ActiveProbeAction.LeaseCompareAndSwap) with
        {
            Reference = "refs/heads/agentrelay-locks/v2_bootstrap_probe",
            ExpectedParent = Head,
            Contenders = [contenderOne, contenderTwo],
            Evidence = "lease-race"
        };

        var result = await transport.ExecuteActiveAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Contains(handler.Bodies, body => body.Contains("\"force\":false", StringComparison.Ordinal));
        Assert.Equal(2, handler.Requests.Count(request => request.Method == HttpMethod.Patch));
    }

    [Fact]
    public async Task WorkerNegativeEvidenceUsesPermissionSnapshotAndSafeReadsWithoutMergeAttempt()
    {
        var handler = new ScriptedHandler(new Dictionary<string, HttpResponseMessage>
        {
            ["GET /installation/repositories"] = Response(HttpStatusCode.OK, "{\"repositories\":[{\"id\":1463547883,\"full_name\":\"StudyOrg88/AgentRelay-v2-sandbox\",\"permissions\":{\"contents\":\"read\",\"pull_requests\":\"read\",\"metadata\":\"read\"}}]}"),
            ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/pulls/1"] = Response(HttpStatusCode.OK, "{\"number\":1,\"head\":{\"sha\":\"" + Head + "\"}}"),
            ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/git/ref/heads/agentrelay-control"] = Response(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"),
            ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/git/ref/heads/agentrelay-locks/v2_bootstrap_probe"] = Response(HttpStatusCode.NotFound, "{\"message\":\"Not Found\"}"),
            ["POST /repos/StudyOrg88/AgentRelay-v2-sandbox/pulls/1/reviews"] = Response(HttpStatusCode.Forbidden, "{\"message\":\"forbidden\"}")
        });
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "worker-token", StageOneConfig(), true, Authorization(ActiveProbeAction.WorkerNegativePermission));
        var request = ActiveRequest(ActiveProbeAction.WorkerNegativePermission) with
        {
            Reference = "refs/heads/agentrelay-control",
            Evidence = "worker-permission-snapshot"
        };

        var result = await transport.ExecuteActiveAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.All(handler.Requests.Where(request => request.Method != HttpMethod.Post), request => Assert.Equal(HttpMethod.Get, request.Method));
        Assert.Equal("/repos/StudyOrg88/AgentRelay-v2-sandbox/pulls/1/reviews", Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post).Path);
        Assert.DoesNotContain(handler.Requests, request => request.Path.Contains("merge", StringComparison.OrdinalIgnoreCase));
    }

    private static BootstrapProbeRequest ReadRequest() => new(Repository, "main", ["sandbox-contract"], "5826689", "5826708", "5826720");

    private static ActiveProbeRequest ActiveRequest(ActiveProbeAction action) =>
        new(Repository, "v2_bootstrap_probe", 1, Head, action == ActiveProbeAction.GateCheck ? TestMerge : Head, action, Identity(action));

    private static ActiveProbeAuthorization Authorization(ActiveProbeAction action) => new(
        Repository, 1, Head, DateTimeOffset.UtcNow.AddMinutes(10), true, "v2_bootstrap_probe", [action],
        "5826689", "5826708", "5826720", "5826750", "5827575", action == ActiveProbeAction.GateCheck ? TestMerge : Head);

    private static string Identity(ActiveProbeAction action) => action switch
    {
        ActiveProbeAction.NativeReview => "5826689",
        ActiveProbeAction.GateCheck => "5826708",
        ActiveProbeAction.LeaseCompareAndSwap => "5827575",
        _ => "5826750"
    };

    private static StageOneGitHubConfig StageOneConfig() => new(
        1463547883, 1, Head, TestMerge, "sandbox-contract", 15368, 27610516,
        5826689, 5826708, 5826720, 5826750, 5827575,
        "refs/heads/agentrelay-control", "refs/heads/agentrelay-locks/v2_bootstrap_probe");

    private static Dictionary<string, HttpResponseMessage> ReadOnlyResponses() => new()
    {
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox"] = Response(HttpStatusCode.OK, "{\"id\":1463547883,\"full_name\":\"StudyOrg88/AgentRelay-v2-sandbox\",\"default_branch\":\"main\"}"),
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/pulls/1"] = Response(HttpStatusCode.OK, "{\"number\":1,\"state\":\"open\",\"base\":{\"ref\":\"main\"},\"head\":{\"sha\":\"" + Head + "\"},\"merge_commit_sha\":\"" + TestMerge + "\"}") ,
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/commits/725dc8bbc799947dd733e10e76d23f67796c16c7/check-runs"] = Response(HttpStatusCode.OK, "{\"total_count\":1,\"check_runs\":[{\"name\":\"sandbox-contract\",\"conclusion\":\"success\",\"app\":{\"id\":15368}}]}") ,
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/branches/main/protection"] = Response(HttpStatusCode.OK, "{\"required_status_checks\":{\"strict\":true,\"checks\":[{\"context\":\"sandbox-contract\",\"app_id\":15368}]},\"enforce_admins\":{\"enabled\":true},\"allow_force_pushes\":{\"enabled\":false},\"allow_deletions\":{\"enabled\":false}}") ,
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/rulesets/27610516"] = Response(HttpStatusCode.OK, RulesetJson()),
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/rulesets?per_page=100"] = Response(HttpStatusCode.OK, "[]"),
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/installations?per_page=100"] = Response(HttpStatusCode.OK, InstallationsJson())
    };

    private static HttpResponseMessage Response(HttpStatusCode status, string json) => new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static string RulesetJson() => "{\"id\":27610516,\"enforcement\":\"active\",\"conditions\":{\"ref_name\":{\"include\":[\"refs/heads/agentrelay-control\",\"refs/heads/agentrelay-locks/**\"]}},\"bypass_actors\":[{\"actor_id\":5827575,\"actor_type\":\"Integration\",\"bypass_mode\":\"always\"}],\"rules\":[{\"type\":\"creation\"},{\"type\":\"update\"},{\"type\":\"deletion\"},{\"type\":\"non_fast_forward\"}]}";

    private static string InstallationsJson() => "{\"installations\":[" +
        "{\"app_id\":5826689,\"repository_selection\":\"selected\",\"permissions\":{\"metadata\":\"read\",\"pull_requests\":\"write\"}}," +
        "{\"app_id\":5826708,\"repository_selection\":\"selected\",\"permissions\":{\"metadata\":\"read\",\"contents\":\"read\",\"pull_requests\":\"read\",\"checks\":\"write\"}}," +
        "{\"app_id\":5826720,\"repository_selection\":\"selected\",\"permissions\":{\"metadata\":\"read\",\"contents\":\"write\",\"pull_requests\":\"write\"}}," +
        "{\"app_id\":5826750,\"repository_selection\":\"selected\",\"permissions\":{\"metadata\":\"read\",\"contents\":\"read\",\"pull_requests\":\"read\"}}," +
        "{\"app_id\":5827575,\"repository_selection\":\"selected\",\"permissions\":{\"metadata\":\"read\",\"contents\":\"write\"}}]}";

    private sealed class ScriptedHandler(Dictionary<string, HttpResponseMessage> responses) : HttpMessageHandler
    {
        private readonly Dictionary<string, int> _counts = [];
        public List<(HttpMethod Method, string Path, AuthenticationHeaderValue? Authorization)> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            Requests.Add((request.Method, path, request.Headers.Authorization));
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            var baseKey = $"{request.Method} {path}";
            var count = _counts.TryGetValue(baseKey, out var current) ? current + 1 : 1;
            _counts[baseKey] = count;
            var key = responses.ContainsKey($"{baseKey}#{count}") ? $"{baseKey}#{count}" : baseKey;
            if (!responses.Remove(key, out var response)) return Response(HttpStatusCode.NotFound, "{\"message\":\"unexpected\"}");
            return response;
        }
    }
}
