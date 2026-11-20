[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Repository,
    [Parameter(Mandatory)] [string] $Base,
    [Parameter(Mandatory)] [string[]] $ExpectedChecks,
    [Parameter(Mandatory)] [string] $ReviewerApp,
    [Parameter(Mandatory)] [string] $GateApp,
    [Parameter(Mandatory)] [string] $MergeApp,
    [string[]] $AllowedTestRepositories = @(),
    [switch] $Active,
    [string] $TestTask,
    [int] $TestPr,
    [string] $ExpectedHead,
    [string] $AuthorizationFile,
    [ValidateSet('native-review', 'gate-check', 'lease-compare-and-swap', 'worker-negative-permission')]
    [string] $ActiveAction,
    [string] $ActiveIdentity,
    [string] $ActiveTarget,
    [string] $ActiveEvidence,
    [string] $ActiveReference,
    [string] $ExpectedParent,
    [string[]] $Contenders = @(),
    [string] $StageOneConfig,
    [switch] $LiveSandbox,
    [switch] $Execute
)

$ErrorActionPreference = 'Stop'

if ($Repository -notmatch '^[^/\s]+/[^/\s]+$') { throw 'Repository must use owner/repo syntax.' }
if ($AllowedTestRepositories -notcontains $Repository) { throw 'Repository is not present in the explicit test-repository allowlist.' }

$arguments = [System.Collections.Generic.List[string]]::new()
$arguments.AddRange([string[]]@('run', '--project', 'src/AgentRelay.Gate', '--', 'probe', '--repo', $Repository, '--base', $Base))
foreach ($check in $ExpectedChecks) { $arguments.Add('--expected-check'); $arguments.Add($check) }
$arguments.AddRange([string[]]@('--reviewer-app', $ReviewerApp, '--gate-app', $GateApp, '--merge-app', $MergeApp))

if ($Execute) {
    if (-not $LiveSandbox -or -not $StageOneConfig -or -not (Test-Path -LiteralPath $StageOneConfig -PathType Leaf)) { throw 'Execution requires explicit -LiveSandbox and an existing stage-one config file.' }
    $arguments.AddRange([string[]]@('--live-sandbox', '--stage-one-config', (Resolve-Path -LiteralPath $StageOneConfig).Path))
}

if ($Active) {
    if (-not $TestTask -or $TestPr -le 0 -or $ExpectedHead -notmatch '^[0-9a-fA-F]{40}$' -or -not $AuthorizationFile -or -not $ActiveAction -or -not $ActiveIdentity -or $ActiveTarget -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'Active probe requires test task, test PR, full expected HEAD, authorization file, exact action, identity, and target commit.'
    }
    if (-not (Test-Path -LiteralPath $AuthorizationFile -PathType Leaf)) { throw 'Authorization file does not exist.' }
    $arguments.AddRange([string[]]@('--active', '--test-task', $TestTask, '--test-pr', [string]$TestPr, '--expected-head', $ExpectedHead, '--authorization-file', (Resolve-Path -LiteralPath $AuthorizationFile).Path, '--active-action', $ActiveAction, '--active-identity', $ActiveIdentity, '--active-target', $ActiveTarget))
    if ($ActiveAction -in @('native-review', 'gate-check')) {
        if (-not $ActiveEvidence) { throw 'Review and gate-check actions require explicit evidence text.' }
        $arguments.AddRange([string[]]@('--evidence', $ActiveEvidence))
    }
    if ($ActiveAction -eq 'lease-compare-and-swap') {
        if ($ActiveReference -ne 'refs/heads/agentrelay-locks/v2_bootstrap_probe' -or $ExpectedParent -notmatch '^[0-9a-fA-F]{40}$' -or $Contenders.Count -ne 2 -or $Contenders.Where({ $_ -notmatch '^[0-9a-fA-F]{40}$' }).Count -gt 0) { throw 'Lease CAS requires the fixed lease ref, full expected parent, and exactly two full-SHA contenders.' }
        $arguments.AddRange([string[]]@('--active-ref', $ActiveReference, '--expected-parent', $ExpectedParent, '--contenders', ($Contenders -join ',')))
    }
    if ($ActiveAction -eq 'worker-negative-permission') {
        if ($ActiveReference -ne 'refs/heads/agentrelay-control') { throw 'Worker negative permission evidence is limited to the fixed control ref.' }
        $arguments.AddRange([string[]]@('--active-ref', $ActiveReference))
    }
}

$tokenName = if (-not $Active) { 'AGENTRELAY_TOKEN_SANDBOX_CONFIG' } else {
    switch ($ActiveAction) {
        'native-review' { 'AGENTRELAY_TOKEN_REVIEWER' }
        'gate-check' { 'AGENTRELAY_TOKEN_GATE' }
        'lease-compare-and-swap' { 'AGENTRELAY_TOKEN_ORCHESTRATOR' }
        'worker-negative-permission' { 'AGENTRELAY_TOKEN_WORKER_1' }
        default { throw 'Active action has no credential mapping.' }
    }
}
# Read-only credential contract:
# - AGENTRELAY_TOKEN_SANDBOX_CONFIG is a repository-limited fine-grained PAT with
#   only the read permissions needed for repository metadata, rules, checks, and
#   the repository's installation list. It is never used as an App installation.
# - Each AGENTRELAY_TOKEN_SANDBOX_SCOPE_* value is a distinct, short-lived GitHub
#   App installation token for the named App, minted without repositories or
#   repository_ids narrowing and permission-downscoped to read-only metadata.
#   It is used only with GET /installation/repositories to prove the full scope.
$scopeCredentialNames = @(
    'AGENTRELAY_TOKEN_SANDBOX_SCOPE_REVIEWER',
    'AGENTRELAY_TOKEN_SANDBOX_SCOPE_GATE',
    'AGENTRELAY_TOKEN_SANDBOX_SCOPE_MERGE',
    'AGENTRELAY_TOKEN_SANDBOX_SCOPE_WORKER',
    'AGENTRELAY_TOKEN_SANDBOX_SCOPE_ORCHESTRATOR'
)
$credentialNames = if ($Active) { @($tokenName) } else { @($tokenName) + $scopeCredentialNames }

if (-not $Execute) {
    [pscustomobject]@{ status = 'DRY_RUN'; repository = $Repository; active = [bool]$Active; liveEnabled = [bool]$LiveSandbox; credentialVariable = $tokenName; credentialVariables = $credentialNames; arguments = [string[]]$arguments } | ConvertTo-Json -Depth 4
    return
}

# Active execution receives one action credential. Read-only execution receives one
# configuration PAT plus five distinct, permission-downscoped, unfiltered installation
# tokens used only to enumerate each App installation's complete repository scope.
$credentials = @{}
foreach ($name in $credentialNames) {
    $value = [Environment]::GetEnvironmentVariable($name, 'Process')
    if (-not $value) { $value = [Environment]::GetEnvironmentVariable($name, 'User') }
    if (-not $value) { throw "$name is not configured." }
    $credentials[$name] = $value
}
if (-not $Active) {
    if (-not $credentials[$tokenName].StartsWith('github_pat_', [System.StringComparison]::Ordinal)) { throw "$tokenName must be a fine-grained personal access token." }
    foreach ($name in $scopeCredentialNames) {
        if (-not $credentials[$name].StartsWith('ghs_', [System.StringComparison]::Ordinal)) { throw "$name must be an unfiltered GitHub App installation access token." }
    }
    if (@($scopeCredentialNames | ForEach-Object { $credentials[$_] } | Select-Object -Unique).Count -ne $scopeCredentialNames.Count) { throw 'Installation-scope credentials must be distinct.' }
}

$process = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$process.UseShellExecute = $false
foreach ($argument in $arguments) { [void]$process.ArgumentList.Add($argument) }
$inheritedRoleTokens = @($process.Environment.Keys | Where-Object { $_.StartsWith('AGENTRELAY_TOKEN_', [System.StringComparison]::OrdinalIgnoreCase) })
foreach ($name in $inheritedRoleTokens) { [void]$process.Environment.Remove($name) }
foreach ($name in $credentialNames) { $process.Environment[$name] = $credentials[$name] }
$process.Environment['GH_TOKEN'] = $credentials[$tokenName]
$child = [System.Diagnostics.Process]::Start($process)
$child.WaitForExit()
exit $child.ExitCode
