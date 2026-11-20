$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $root 'scripts/invoke-v2-bootstrap-probe.ps1'
$scriptText = Get-Content -Raw -LiteralPath $scriptPath

if ($scriptText -match '(?i)--admin|--delete-branch|gh\s+pr\s+merge|ruleset|/rules') { throw 'Bootstrap script contains a forbidden merge or repository-rules operation.' }
foreach ($required in @('AllowedTestRepositories', 'DRY_RUN', 'AuthorizationFile', 'ExpectedHead', 'TestPr', 'TestTask', 'ActiveAction', 'ActiveIdentity', 'ActiveTarget', '--active-action', '--active-identity', '--active-target', 'AGENTRELAY_TOKEN_SANDBOX_CONFIG', 'AGENTRELAY_TOKEN_REVIEWER', 'AGENTRELAY_TOKEN_GATE', 'AGENTRELAY_TOKEN_ORCHESTRATOR', 'AGENTRELAY_TOKEN_WORKER_1', 'credentialVariable', 'credentialVariables', 'GH_TOKEN', "StartsWith('AGENTRELAY_TOKEN_'", 'Environment.Remove', '$process.Environment[$tokenName] = $token', 'ActiveEvidence', 'ActiveReference', 'ExpectedParent', 'Contenders', 'LiveSandbox', 'StageOneConfig', '-active-ref', '-expected-parent', '-contenders')) {
    if ($scriptText -notmatch [regex]::Escape($required)) { throw "Bootstrap script is missing required boundary: $required" }
}
if ($scriptText -notmatch "Environment\['GH_TOKEN'\] = \`$token") { throw 'The selected primary token must be copied to GH_TOKEN without exposing any credential.' }

$dryRun = & $scriptPath -Repository 'example/test-repo' -Base main -ExpectedChecks build -ReviewerApp reviewer -GateApp gate -MergeApp merge -AllowedTestRepositories 'example/test-repo' | ConvertFrom-Json
if ($dryRun.status -ne 'DRY_RUN' -or $dryRun.active) { throw 'Default bootstrap invocation must remain an inactive dry run.' }
if (@($dryRun.credentialVariables).Count -ne 1 -or $dryRun.credentialVariables[0] -ne 'AGENTRELAY_TOKEN_SANDBOX_CONFIG') {
    throw 'Read-only bootstrap must accept only the configuration PAT; pre-minted installation tokens are prohibited.'
}

$authorizationFile = [System.IO.Path]::GetTempFileName()
Set-Content -LiteralPath $authorizationFile -Value '{}'
$roleMappings = @{
    'native-review' = 'AGENTRELAY_TOKEN_REVIEWER'
    'gate-check' = 'AGENTRELAY_TOKEN_GATE'
    'lease-compare-and-swap' = 'AGENTRELAY_TOKEN_ORCHESTRATOR'
    'worker-negative-permission' = 'AGENTRELAY_TOKEN_WORKER_1'
}
foreach ($entry in $roleMappings.GetEnumerator()) {
    $contenders = @(('b' * 40), ('c' * 40))
    $activeReference = if ($entry.Key -eq 'worker-negative-permission') { 'refs/heads/agentrelay-control' } else { 'refs/heads/agentrelay-locks/v2_bootstrap_probe' }
    $activeDryRun = & $scriptPath -Repository 'example/test-repo' -Base main -ExpectedChecks build -ReviewerApp reviewer -GateApp gate -MergeApp merge -AllowedTestRepositories 'example/test-repo' -Active -TestTask task -TestPr 1 -ExpectedHead ('a' * 40) -AuthorizationFile $authorizationFile -ActiveAction $entry.Key -ActiveIdentity identity -ActiveTarget ('a' * 40) -ActiveEvidence 'fixture evidence' -ActiveReference $activeReference -ExpectedParent ('a' * 40) -Contenders $contenders | ConvertFrom-Json
    if ($activeDryRun.credentialVariable -ne $entry.Value) { throw "Active action $($entry.Key) selected wrong credential variable: $($activeDryRun.credentialVariable)" }
}

$failed = $false
try {
    & $scriptPath -Repository 'example/test-repo' -Base main -ExpectedChecks build -ReviewerApp reviewer -GateApp gate -MergeApp merge -AllowedTestRepositories 'example/test-repo' -Active -TestTask task -TestPr 1 -ExpectedHead ('a' * 40) -ErrorAction Stop
} catch { $failed = $true }
if (-not $failed) { throw 'Active probe without an authorization file must fail.' }

Write-Output 'v2 bootstrap contract: PASS'
