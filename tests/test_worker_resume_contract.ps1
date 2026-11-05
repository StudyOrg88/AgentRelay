$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$skill = Get-Content (Join-Path $root 'skills/worker/SKILL.md') -Raw

foreach ($contract in @(
    'agentrelay-task',
    'Issue URL',
    'task_id',
    'task/<task_id>',
    'code_defect',
    'evidence_gap',
    'contract_ambiguity',
    'out_of_scope',
    'Orchestrator',
    'finding ID',
    '同一个 PR',
    '不得自行出具 review PASS'
)) {
    if ($skill -notmatch [regex]::Escape($contract)) { throw "Missing worker resume contract: $contract" }
}

if ($skill -notmatch '(?s)code_defect.{0,450}Worker|Worker.{0,450}code_defect') { throw 'Worker must fix concrete in-scope findings' }
if ($skill -notmatch '(?s)contract_ambiguity.{0,450}Orchestrator') { throw 'Contract ambiguity must stop Worker and route to Orchestrator' }
if ($skill -notmatch '(?s)若已存在.{0,300}在该分支/同一个 PR 上继续') { throw 'Existing task work must be resumed without replacement' }

Write-Output 'PASS worker resume contract'
