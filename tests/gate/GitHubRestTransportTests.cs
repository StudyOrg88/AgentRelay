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
    private const string BaseSha = "c5d0f7ad2f6091a7e9238a1224412fb5be8e1101";

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
            File.WriteAllText(authPath, JsonSerializer.Serialize(Authorization(ActiveProbeAction.NativeReview) with { Evidence = "review probe" }));
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
    public async Task ActiveOperationCannotChangeEvidenceFromStageTwoAuthorization()
    {
        var handler = new ScriptedHandler(new Dictionary<string, HttpResponseMessage>());
        var authorization = Authorization(ActiveProbeAction.NativeReview) with { Evidence = "authorized evidence" };
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "active-token", StageOneConfig(), true, authorization);

        await Assert.ThrowsAsync<GitHubTransportException>(() => transport.ExecuteActiveAsync(
            ActiveRequest(ActiveProbeAction.NativeReview) with { Evidence = "different evidence" }, CancellationToken.None));

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

    [Theory]
    [InlineData(false, true, true, "725dc8bbc799947dd733e10e76d23f67796c16c7")]
    [InlineData(true, false, false, "354099dbca836c3cf0e01b7b4bd637279ad80fa9")]
    [InlineData(true, true, true, "725dc8bbc799947dd733e10e76d23f67796c16c7")]
    public async Task ReadOnlyDiscoversRequiredCheckTargetFromGitHubSemantics(bool headCheck, bool mergeCheck, bool strict, string expectedTarget)
    {
        var responses = ReadOnlyResponses(
            HeadChecks(headCheck), MergeChecks(mergeCheck), strict,
            head: Head,
            mergeSha: TestMerge);
        var transport = new GitHubRestBootstrapTransport(new HttpClient(new ScriptedHandler(responses)), "read-token", StageOneConfig());

        var evidence = await transport.ReadEvidenceAsync(ReadRequest(), CancellationToken.None);

        Assert.Equal(expectedTarget, evidence!.GateTargetCommit);
    }

    [Theory]
    [InlineData(false, false, 15368, "success")]
    [InlineData(true, false, 999, "success")]
    [InlineData(true, false, 15368, "failure")]
    public async Task ReadOnlyRejectsMissingFailedOrWrongSourceRequiredCheck(bool headCheck, bool mergeCheck, long appId, string conclusion)
    {
        var responses = ReadOnlyResponses(
            HeadChecks(headCheck, appId, conclusion), MergeChecks(mergeCheck, appId, conclusion),
            strict: true, head: Head, mergeSha: TestMerge);
        var transport = new GitHubRestBootstrapTransport(new HttpClient(new ScriptedHandler(responses)), "read-token", StageOneConfig());

        var error = await Assert.ThrowsAsync<GitHubTransportException>(() => transport.ReadEvidenceAsync(ReadRequest(), CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyRejectsRequiredCheckRunWithShortCommitSha()
    {
        var shortRun = "[{\"name\":\"sandbox-contract\",\"status\":\"completed\",\"conclusion\":\"success\",\"head_sha\":\"" + TestMerge[..12] + "\",\"app\":{\"id\":15368}}]";
        var responses = ReadOnlyResponses(HeadChecks(false), shortRun, strict: true, head: Head, mergeSha: TestMerge);
        var transport = new GitHubRestBootstrapTransport(new HttpClient(new ScriptedHandler(responses)), "read-token", StageOneConfig());

        var error = await Assert.ThrowsAsync<GitHubTransportException>(() => transport.ReadEvidenceAsync(ReadRequest(), CancellationToken.None));

        Assert.Equal(403, error.StatusCode);
    }

    [Fact]
    public async Task ReadOnlyFailsClosedOnForbiddenEmptyOrMismatchedFacts()
    {
        var responses = ReadOnlyResponses(HeadChecks(false), MergeChecks(true, 999), strict: true, head: Head, mergeSha: TestMerge);
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
        var responses = action == ActiveProbeAction.GateCheck
            ? ReadOnlyResponses(HeadChecks(false), MergeChecks(true), strict: true, head: Head, mergeSha: TestMerge)
            : new Dictionary<string, HttpResponseMessage>();
        if (action == ActiveProbeAction.GateCheck) AddRepeatedDiscovery(responses);
        responses[$"{method} {path}"] = action == ActiveProbeAction.NativeReview
            ? Response(HttpStatusCode.OK, "{\"id\":42,\"html_url\":\"https://github.com/review/42\",\"commit_id\":\"" + Head + "\",\"performed_via_github_app\":{\"id\":5826689}}")
            : Response(HttpStatusCode.Created, "{\"id\":43,\"html_url\":\"https://github.com/check/43\",\"head_sha\":\"" + TestMerge + "\",\"app\":{\"id\":5826708}}");
        var handler = new ScriptedHandler(responses);
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "active-token", StageOneConfig(), true, Authorization(action));
        var request = ActiveRequest(action) with { Evidence = "probe-evidence" };

        var result = await transport.ExecuteActiveAsync(request, CancellationToken.None);

        Assert.NotNull(result);
        Assert.Equal(action == ActiveProbeAction.GateCheck ? 11 : 1, handler.Requests.Count);
        Assert.Contains(handler.Requests, request => request.Path == path && request.Method == HttpMethod.Post);
        Assert.DoesNotContain("probe-evidence", handler.Bodies[^1]);
        Assert.Contains(ActiveProbeEvidence.Digest("probe-evidence"), handler.Bodies[^1]);
        Assert.DoesNotContain(handler.Requests[0].Path, "merge", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GatePublicationRevalidatesSnapshotAndUsesDiscoveredTarget()
    {
        var responses = ReadOnlyResponses(HeadChecks(false), MergeChecks(true), strict: true, head: Head, mergeSha: TestMerge);
        AddRepeatedDiscovery(responses);
        responses["POST /repos/StudyOrg88/AgentRelay-v2-sandbox/check-runs"] = Response(HttpStatusCode.Created,
            "{\"id\":43,\"html_url\":\"https://github.com/check/43\",\"head_sha\":\"" + TestMerge + "\",\"app\":{\"id\":5826708}}");
        var handler = new ScriptedHandler(responses);
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "active-token", StageOneConfig(), true, Authorization(ActiveProbeAction.GateCheck));

        var result = await transport.ExecuteActiveAsync(ActiveRequest(ActiveProbeAction.GateCheck) with { Evidence = "probe-evidence" }, CancellationToken.None);

        Assert.Equal(TestMerge, result!.TargetCommit);
        Assert.Equal(2, handler.Requests.Count(request => request.Path == "/repos/StudyOrg88/AgentRelay-v2-sandbox/commits/" + Head + "/check-runs"));
        Assert.Equal(2, handler.Requests.Count(request => request.Path == "/repos/StudyOrg88/AgentRelay-v2-sandbox/commits/" + TestMerge + "/check-runs"));
        Assert.Equal(TestMerge, JsonDocument.Parse(handler.Bodies[^1]).RootElement.GetProperty("head_sha").GetString());
    }

    [Fact]
    public async Task GatePublicationUsesHeadWhenGitHubRequiresHeadChecks()
    {
        var responses = ReadOnlyResponses(HeadChecks(true), MergeChecks(false), strict: false, head: Head, mergeSha: TestMerge);
        AddRepeatedDiscovery(responses);
        responses["POST /repos/StudyOrg88/AgentRelay-v2-sandbox/check-runs"] = Response(HttpStatusCode.Created,
            "{\"id\":43,\"html_url\":\"https://github.com/check/43\",\"head_sha\":\"" + Head + "\",\"app\":{\"id\":5826708}}");
        var handler = new ScriptedHandler(responses);
        var authorization = Authorization(ActiveProbeAction.GateCheck) with { ExpectedTargetCommit = Head };
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "active-token", StageOneConfig(), true, authorization);

        var result = await transport.ExecuteActiveAsync(ActiveRequest(ActiveProbeAction.GateCheck) with
        {
            ExpectedTargetCommit = Head,
            Evidence = "probe-evidence"
        }, CancellationToken.None);

        Assert.Equal(Head, result!.TargetCommit);
        Assert.Equal(Head, JsonDocument.Parse(handler.Bodies[^1]).RootElement.GetProperty("head_sha").GetString());
    }

    [Fact]
    public async Task GatePublicationFailsClosedWhenSnapshotDriftsBeforePublishing()
    {
        var responses = ReadOnlyResponses(HeadChecks(false), MergeChecks(true), strict: true, head: new string('a', 40), mergeSha: TestMerge);
        var handler = new ScriptedHandler(responses);
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "active-token", StageOneConfig(), true, Authorization(ActiveProbeAction.GateCheck));

        await Assert.ThrowsAsync<GitHubTransportException>(() => transport.ExecuteActiveAsync(
            ActiveRequest(ActiveProbeAction.GateCheck) with { Evidence = "probe-evidence" }, CancellationToken.None));

        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task GatePublicationFailsClosedWhenBaseShaDiffersFromAuthorizedDiscovery()
    {
        var responses = ReadOnlyResponses(HeadChecks(false), MergeChecks(true), strict: true, head: Head, mergeSha: TestMerge);
        AddRepeatedDiscovery(responses);
        ReplaceResponseBody(responses, "GET /repos/StudyOrg88/AgentRelay-v2-sandbox/pulls/1", body => body.Replace(BaseSha, new string('d', 40), StringComparison.Ordinal));
        ReplaceResponseBody(responses, "GET /repos/StudyOrg88/AgentRelay-v2-sandbox/pulls/1#2", body => body.Replace(BaseSha, new string('d', 40), StringComparison.Ordinal));
        responses["POST /repos/StudyOrg88/AgentRelay-v2-sandbox/check-runs"] = Response(HttpStatusCode.Created,
            "{\"id\":43,\"html_url\":\"https://github.com/check/43\",\"head_sha\":\"" + TestMerge + "\",\"app\":{\"id\":5826708}}");
        var handler = new ScriptedHandler(responses);
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "active-token", StageOneConfig(), true, Authorization(ActiveProbeAction.GateCheck));

        await Assert.ThrowsAsync<GitHubTransportException>(() => transport.ExecuteActiveAsync(
            ActiveRequest(ActiveProbeAction.GateCheck) with { Evidence = "probe-evidence" }, CancellationToken.None));

        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    [Theory]
    [InlineData("base")]
    [InlineData("test-merge")]
    [InlineData("required-check")]
    [InlineData("target-result")]
    public async Task GatePublicationFailsClosedWhenAnyRequiredSnapshotFactDrifts(string fact)
    {
        var responses = ReadOnlyResponses(HeadChecks(false), MergeChecks(true), strict: true, head: Head, mergeSha: TestMerge);
        AddRepeatedDiscovery(responses);
        var prKey = "GET /repos/StudyOrg88/AgentRelay-v2-sandbox/pulls/1#2";
        var protectionKey = "GET /repos/StudyOrg88/AgentRelay-v2-sandbox/branches/main/protection#2";
        var mergeChecksKey = "GET /repos/StudyOrg88/AgentRelay-v2-sandbox/commits/" + TestMerge + "/check-runs#2";
        if (fact == "base")
            ReplaceResponseBody(responses, prKey, body => body.Replace("\"ref\":\"main\"", "\"ref\":\"changed\"", StringComparison.Ordinal));
        else if (fact == "test-merge")
            ReplaceResponseBody(responses, prKey, body => body.Replace(TestMerge, new string('b', 40), StringComparison.Ordinal));
        else if (fact == "required-check")
            ReplaceResponseBody(responses, protectionKey, body => body.Replace("\"app_id\":15368", "\"app_id\":999", StringComparison.Ordinal));
        else
            ReplaceResponseBody(responses, mergeChecksKey, _ => CheckRunsJson("[]"));
        var handler = new ScriptedHandler(responses);
        var transport = new GitHubRestBootstrapTransport(new HttpClient(handler), "active-token", StageOneConfig(), true, Authorization(ActiveProbeAction.GateCheck));

        await Assert.ThrowsAsync<GitHubTransportException>(() => transport.ExecuteActiveAsync(
            ActiveRequest(ActiveProbeAction.GateCheck) with { Evidence = "probe-evidence" }, CancellationToken.None));

        Assert.DoesNotContain(handler.Requests, request => request.Method == HttpMethod.Post);
    }

    private static void AddRepeatedDiscovery(Dictionary<string, HttpResponseMessage> responses)
    {
        foreach (var path in new[]
        {
            "GET /repos/StudyOrg88/AgentRelay-v2-sandbox",
            "GET /repos/StudyOrg88/AgentRelay-v2-sandbox/pulls/1",
            "GET /repos/StudyOrg88/AgentRelay-v2-sandbox/branches/main/protection",
            "GET /repos/StudyOrg88/AgentRelay-v2-sandbox/commits/" + Head + "/check-runs",
            "GET /repos/StudyOrg88/AgentRelay-v2-sandbox/commits/" + TestMerge + "/check-runs"
        })
        {
            var first = responses[path];
            var body = first.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            responses[path + "#2"] = Response(first.StatusCode, body);
        }
    }

    private static void ReplaceResponseBody(Dictionary<string, HttpResponseMessage> responses, string key, Func<string, string> replace)
    {
        var response = responses[key];
        var body = response.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
        responses[key] = Response(response.StatusCode, replace(body));
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
        Assert.DoesNotContain("worker-permission-snapshot", handler.Bodies[^1]);
        Assert.Contains(ActiveProbeEvidence.Digest("worker-permission-snapshot"), handler.Bodies[^1]);
        Assert.DoesNotContain(handler.Requests, request => request.Path.Contains("merge", StringComparison.OrdinalIgnoreCase));
    }

    private static BootstrapProbeRequest ReadRequest() => new(Repository, "main", ["sandbox-contract"], "5826689", "5826708", "5826720");

    private static ActiveProbeRequest ActiveRequest(ActiveProbeAction action) =>
        new(Repository, "v2_bootstrap_probe", 1, Head, action == ActiveProbeAction.GateCheck ? TestMerge : Head, action, Identity(action));

    private static ActiveProbeAuthorization Authorization(ActiveProbeAction action) => new(
        Repository, 1, Head, DateTimeOffset.UtcNow.AddMinutes(10), true, "v2_bootstrap_probe", [action],
        "5826689", "5826708", "5826720", "5826750", "5827575", action == ActiveProbeAction.GateCheck ? TestMerge : Head,
        action == ActiveProbeAction.LeaseCompareAndSwap ? "refs/heads/agentrelay-locks/v2_bootstrap_probe" : action == ActiveProbeAction.WorkerNegativePermission ? "refs/heads/agentrelay-control" : null,
        action == ActiveProbeAction.LeaseCompareAndSwap ? Head : null,
        action == ActiveProbeAction.LeaseCompareAndSwap ? [new string('a', 40), new string('b', 40)] : null,
        action switch
        {
            ActiveProbeAction.LeaseCompareAndSwap => "lease-race",
            ActiveProbeAction.WorkerNegativePermission => "worker-permission-snapshot",
            _ => "probe-evidence"
        }, action == ActiveProbeAction.GateCheck ? BaseSha : null);

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

    private static Dictionary<string, HttpResponseMessage> ReadOnlyResponses(string headRuns = "[]", string mergeRuns = "[{\"name\":\"sandbox-contract\",\"status\":\"completed\",\"conclusion\":\"success\",\"head_sha\":\"725dc8bbc799947dd733e10e76d23f67796c16c7\",\"app\":{\"id\":15368}}]", bool strict = true, string? head = null, string? mergeSha = null) => new()
    {
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox"] = Response(HttpStatusCode.OK, "{\"id\":1463547883,\"full_name\":\"StudyOrg88/AgentRelay-v2-sandbox\",\"default_branch\":\"main\"}"),
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/pulls/1"] = Response(HttpStatusCode.OK, "{\"number\":1,\"state\":\"open\",\"base\":{\"ref\":\"main\",\"sha\":\"" + BaseSha + "\"},\"head\":{\"sha\":\"" + (head ?? Head) + "\"},\"merge_commit_sha\":\"" + (mergeSha ?? TestMerge) + "\"}") ,
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/commits/" + Head + "/check-runs"] = Response(HttpStatusCode.OK, CheckRunsJson(headRuns)),
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/commits/" + TestMerge + "/check-runs"] = Response(HttpStatusCode.OK, CheckRunsJson(mergeRuns)),
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/branches/main/protection"] = Response(HttpStatusCode.OK, "{\"required_status_checks\":{\"strict\":" + strict.ToString().ToLowerInvariant() + ",\"checks\":[{\"context\":\"sandbox-contract\",\"app_id\":15368}]},\"enforce_admins\":{\"enabled\":true},\"allow_force_pushes\":{\"enabled\":false},\"allow_deletions\":{\"enabled\":false}}") ,
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/rulesets/27610516"] = Response(HttpStatusCode.OK, RulesetJson()),
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/rulesets?per_page=100"] = Response(HttpStatusCode.OK, "[]"),
        ["GET /repos/StudyOrg88/AgentRelay-v2-sandbox/installations?per_page=100"] = Response(HttpStatusCode.OK, InstallationsJson())
    };

    private static string CheckRunsJson(string runs)
    {
        using var document = JsonDocument.Parse(runs);
        return "{\"total_count\":" + document.RootElement.GetArrayLength() + ",\"check_runs\":" + runs + "}";
    }

    private static string HeadChecks(bool present, long appId = 15368, string conclusion = "success") => present
        ? "[{\"name\":\"sandbox-contract\",\"status\":\"completed\",\"conclusion\":\"" + conclusion + "\",\"head_sha\":\"" + Head + "\",\"app\":{\"id\":" + appId + "}}]"
        : "[]";

    private static string MergeChecks(bool present, long appId = 15368, string conclusion = "success") => present
        ? "[{\"name\":\"sandbox-contract\",\"status\":\"completed\",\"conclusion\":\"" + conclusion + "\",\"head_sha\":\"" + TestMerge + "\",\"app\":{\"id\":" + appId + "}}]"
        : "[]";

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
