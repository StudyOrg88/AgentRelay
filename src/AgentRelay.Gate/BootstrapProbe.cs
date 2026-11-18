namespace AgentRelay.Gate;

public sealed class BootstrapProbe
{
    public async Task<BootstrapProbeResult> EvaluateAsync(BootstrapProbeRequest request, IGitHubApi api, CancellationToken cancellationToken)
    {
        var requestBlockers = ValidateRequest(request);
        if (requestBlockers.Count > 0)
            return Blocked(requestBlockers);

        BootstrapEvidence evidence;
        try
        {
            evidence = await api.ReadBootstrapEvidenceAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return Blocked(["github_evidence_unavailable"]);
        }

        var blockers = new List<string>();
        if (!evidence.PermissionsKnown) blockers.Add("permissions_unknown");
        if (evidence.ExpectedChecks.Count == 0) blockers.Add("expected_checks_empty");
        if (request.ExpectedChecks.Any(check => !evidence.ExpectedChecks.Contains(check, StringComparer.Ordinal))) blockers.Add("expected_checks_missing");
        if (evidence.WorkerCanMerge) blockers.Add("worker_can_merge");
        if (evidence.WorkerCanBypass) blockers.Add("worker_can_bypass");
        if (evidence.WorkerCanWriteControlRefs) blockers.Add("worker_can_write_control_refs");
        if (new[] { evidence.ReviewerIdentity, evidence.GateIdentity, evidence.MergeIdentity }.Distinct(StringComparer.Ordinal).Count() != 3) blockers.Add("identities_not_isolated");
        if (!StringComparer.Ordinal.Equals(evidence.ReviewerIdentity, request.ReviewerApp) ||
            !StringComparer.Ordinal.Equals(evidence.GateIdentity, request.GateApp) ||
            !StringComparer.Ordinal.Equals(evidence.MergeIdentity, request.MergeApp)) blockers.Add("identity_mismatch");
        if (!StringComparer.Ordinal.Equals(evidence.GateCheckApp, request.GateApp)) blockers.Add("gate_app_mismatch");
        if (evidence.MergeQueueEnabled) blockers.Add("merge_queue_unsupported");
        if (!evidence.LeaseCapabilityKnown) blockers.Add("lease_capability_unknown");
        else
        {
            if (!evidence.LeaseCreateUsesNonForceCas) blockers.Add("lease_non_force_cas_unsupported");
            if (!evidence.LeaseCompetitionSingleWinner) blockers.Add("lease_competition_unsupported");
        }
        if (!evidence.GateTargetKnown) blockers.Add("gate_target_unknown");
        else
        {
            if (evidence.GateTargetCommit is null || evidence.GateTargetCommit.Length != 40 || !evidence.GateTargetCommit.All(Uri.IsHexDigit)) blockers.Add("gate_target_invalid");
            if (!evidence.GateTargetsRequiredCommit) blockers.Add("gate_target_mismatch");
            if (!evidence.GateTargetAssociatedWithReviewedHead) blockers.Add("gate_target_unassociated");
        }

        return blockers.Count == 0
            ? new BootstrapProbeResult(ProbeStatus.Ready, [], "all required bootstrap evidence is present")
            : Blocked(blockers);
    }

    private static List<string> ValidateRequest(BootstrapProbeRequest request)
    {
        var blockers = new List<string>();
        var parts = request.Repository.Split('/');
        if (parts.Length != 2 || parts.Any(string.IsNullOrWhiteSpace) || request.Repository.Contains("github.com", StringComparison.OrdinalIgnoreCase)) blockers.Add("invalid_repository");
        if (string.IsNullOrWhiteSpace(request.Base)) blockers.Add("base_required");
        if (request.ExpectedChecks.Count == 0 || request.ExpectedChecks.Any(string.IsNullOrWhiteSpace)) blockers.Add("expected_checks_empty");
        if (new[] { request.ReviewerApp, request.GateApp, request.MergeApp }.Any(string.IsNullOrWhiteSpace)) blockers.Add("identity_required");
        if (new[] { request.ReviewerApp, request.GateApp, request.MergeApp }.Distinct(StringComparer.Ordinal).Count() != 3) blockers.Add("identities_not_isolated");
        return blockers;
    }

    private static BootstrapProbeResult Blocked(IReadOnlyList<string> blockers)
        => new(ProbeStatus.Blocked, blockers, "bootstrap evidence is incomplete or incompatible");
}
