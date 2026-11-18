$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $root 'scripts/invoke-v2-bootstrap-probe.ps1'
$scriptText = Get-Content -Raw -LiteralPath $scriptPath

if ($scriptText -match '(?i)--admin|--delete-branch|gh\s+pr\s+merge|ruleset|/rules') { throw 'Bootstrap script contains a forbidden merge or repository-rules operation.' }
foreach ($required in @('AllowedTestRepositories', 'DRY_RUN', 'AuthorizationFile', 'ExpectedHead', 'TestPr', 'TestTask', 'GH_TOKEN', "StartsWith('AGENTRELAY_TOKEN_'", 'Environment.Remove', '$process.Environment[$tokenName] = $token')) {
    if ($scriptText -notmatch [regex]::Escape($required)) { throw "Bootstrap script is missing required boundary: $required" }
}
if ($scriptText -notmatch "Environment\['GH_TOKEN'\] = \`$token") { throw 'Selected role token must be the only token copied to GH_TOKEN.' }

$dryRun = & $scriptPath -Repository 'example/test-repo' -Base main -ExpectedChecks build -ReviewerApp reviewer -GateApp gate -MergeApp merge -AllowedTestRepositories 'example/test-repo' | ConvertFrom-Json
if ($dryRun.status -ne 'DRY_RUN' -or $dryRun.active) { throw 'Default bootstrap invocation must remain an inactive dry run.' }

$failed = $false
try {
    & $scriptPath -Repository 'example/test-repo' -Base main -ExpectedChecks build -ReviewerApp reviewer -GateApp gate -MergeApp merge -AllowedTestRepositories 'example/test-repo' -Active -TestTask task -TestPr 1 -ExpectedHead ('a' * 40) -ErrorAction Stop
} catch { $failed = $true }
if (-not $failed) { throw 'Active probe without an authorization file must fail.' }

Write-Output 'v2 bootstrap contract: PASS'
