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
    if (-not $TestTask -or $TestPr -le 0 -or $ExpectedHead -notmatch '^[0-9a-fA-F]{40}$' -or -not $AuthorizationFile) {
        throw 'Active probe requires test task, test PR, full expected HEAD, and authorization file.'
    }
    if (-not (Test-Path -LiteralPath $AuthorizationFile -PathType Leaf)) { throw 'Authorization file does not exist.' }
    $arguments.AddRange([string[]]@('--active', '--test-task', $TestTask, '--test-pr', [string]$TestPr, '--expected-head', $ExpectedHead, '--authorization-file', (Resolve-Path -LiteralPath $AuthorizationFile).Path))
}

if (-not $Execute) {
    [pscustomobject]@{ status = 'DRY_RUN'; repository = $Repository; active = [bool]$Active; arguments = [string[]]$arguments } | ConvertTo-Json -Depth 4
    return
}

# A single role token is selected for the child only; it is never printed or persisted.
$tokenName = if ($Active) { 'AGENTRELAY_TOKEN_GATE' } else { 'AGENTRELAY_TOKEN_ORCHESTRATOR' }
$token = [Environment]::GetEnvironmentVariable($tokenName, 'Process')
if (-not $token) { $token = [Environment]::GetEnvironmentVariable($tokenName, 'User') }
if (-not $token) { throw "$tokenName is not configured." }

$process = [System.Diagnostics.ProcessStartInfo]::new('dotnet')
$process.UseShellExecute = $false
foreach ($argument in $arguments) { [void]$process.ArgumentList.Add($argument) }
$process.Environment['GH_TOKEN'] = $token
$child = [System.Diagnostics.Process]::Start($process)
$child.WaitForExit()
exit $child.ExitCode
