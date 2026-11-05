$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$skillPath = Join-Path $root 'skills/task-entry/SKILL.md'
if (-not (Test-Path $skillPath)) { throw 'Missing task-entry skill' }
$skill = Get-Content $skillPath -Raw

foreach ($contract in @(
    'name: agentrelay-task',
    'GitHub Issue URL',
    'repository identity',
    'task:planned',
    'task:in-progress',
    'task:review',
    'next_role',
    'proceed|handoff|blocked|complete',
    'worker',
    'reviewer',
    'orchestrator',
    'Acceptance v1',
    'fail closed',
    '不选择设备',
    '网络/API 失败'
)) {
    if ($skill -notmatch [regex]::Escape($contract)) { throw "Missing task-entry contract: $contract" }
}

if ($skill -notmatch '(?s)task:planned.{0,500}worker') { throw 'Planned tasks must route to Worker' }
if ($skill -notmatch '(?s)ready for independent review.{0,250}reviewer') { throw 'Review-ready tasks must route to Reviewer' }
if ($skill -notmatch '(?s)contract_ambiguity.{0,400}orchestrator') { throw 'Contract ambiguity must route to Orchestrator' }
if ($skill -notmatch '(?s)code_defect.{0,400}worker') { throw 'In-scope code defects must route to Worker' }
if ($skill -notmatch 'GitHub is unreachable' -or $skill -notmatch 'stop before making writes') { throw 'GitHub failures must stop before writes' }
$reviewFinding = $skill.IndexOf('Current review has a concrete')
$reviewNeeded = $skill.IndexOf('A matching PR is ready for independent review')
if ($reviewFinding -lt 0 -or $reviewNeeded -lt 0 -or $reviewFinding -gt $reviewNeeded) {
    throw 'Unresolved review findings must take precedence over the generic missing-PASS review route'
}

Write-Output 'PASS task-entry contract'
