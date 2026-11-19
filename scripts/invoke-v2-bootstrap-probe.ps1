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
    [switch] $Execute
)

$ErrorActionPreference = 'Stop'

if ($Repository -notmatch '^[^/\s]+/[^/\s]+$') { throw 'Repository must use owner/repo syntax.' }
if ($AllowedTestRepositories -notcontains $Repository) { throw 'Repository is not present in the explicit test-repository allowlist.' }

$arguments = [System.Collections.Generic.List[string]]::new()
$arguments.AddRange([string[]]@('run', '--project', 'src/AgentRelay.Gate', '--', 'probe', '--repo', $Repository, '--base', $Base))
foreach ($check in $ExpectedChecks) { $arguments.Add('--expected-check'); $arguments.Add($check) }
$arguments.AddRange([string[]]@('--reviewer-app', $ReviewerApp, '--gate-app', $GateApp, '--merge-app', $MergeApp))

if ($Active) {
    if (-not $TestTask -or $TestPr -le 0 -or $ExpectedHead -notmatch '^[0-9a-fA-F]{40}$' -or -not $AuthorizationFile -or -not $ActiveAction -or -not $ActiveIdentity -or $ActiveTarget -notmatch '^[0-9a-fA-F]{40}$') {
        throw 'Active probe requires test task, test PR, full expected HEAD, authorization file, exact action, identity, and target commit.'
    }
    if (-not (Test-Path -LiteralPath $AuthorizationFile -PathType Leaf)) { throw 'Authorization file does not exist.' }
    $arguments.AddRange([string[]]@('--active', '--test-task', $TestTask, '--test-pr', [string]$TestPr, '--expected-head', $ExpectedHead, '--authorization-file', (Resolve-Path -LiteralPath $AuthorizationFile).Path, '--active-action', $ActiveAction, '--active-identity', $ActiveIdentity, '--active-target', $ActiveTarget))
}

$tokenName = if (-not $Active) { 'AGENTRELAY_TOKEN_ORCHESTRATOR' } else {
    switch ($ActiveAction) {
        'native-review' { 'AGENTRELAY_TOKEN_REVIEWER' }
        'gate-check' { 'AGENTRELAY_TOKEN_GATE' }
        'lease-compare-and-swap' { 'AGENTRELAY_TOKEN_ORCHESTRATOR' }
        'worker-negative-permission' { 'AGENTRELAY_TOKEN_WORKER_1' }
        default { throw 'Active action has no credential mapping.' }
    }
}

if (-not $Execute) {
    [pscustomobject]@{ status = 'DRY_RUN'; repository = $Repository; active = [bool]$Active; credentialVariable = $tokenName; arguments = [string[]]$arguments } | ConvertTo-Json -Depth 4
    return
}

# A single role token is selected for the child only; it is never printed or persisted.
$token = [Environment]::GetEnvironmentVariable($tokenName, 'Process')
if (-not $token) { $token = [Environment]::GetEnvironmentVariable($tokenName, 'User') }
if (-not $token) { throw "$tokenName is not configured." }

$process = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$process.UseShellExecute = $false
foreach ($argument in $arguments) { [void]$process.ArgumentList.Add($argument) }
$inheritedRoleTokens = @($process.Environment.Keys | Where-Object { $_.StartsWith('AGENTRELAY_TOKEN_', [System.StringComparison]::OrdinalIgnoreCase) })
foreach ($name in $inheritedRoleTokens) { [void]$process.Environment.Remove($name) }
$process.Environment[$tokenName] = $token
$process.Environment['GH_TOKEN'] = $token
$child = [System.Diagnostics.Process]::Start($process)
$child.WaitForExit()
exit $child.ExitCode
