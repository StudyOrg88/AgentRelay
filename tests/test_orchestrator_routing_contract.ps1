$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$skill = Get-Content (Join-Path $root 'skills/orchestrator/SKILL.md') -Raw

foreach ($contract in @(
    'contract_ambiguity',
    'out_of_scope',
    'code_defect',
    'evidence_gap',
    '版本化',
    'follow-up Issue',
    'Acceptance v1',
    '当前完整 HEAD SHA',
    'required CI',
    'gh pr merge --squash',
    'fail closed'
)) {
    if ($skill -notmatch [regex]::Escape($contract)) { throw "Missing orchestrator routing contract: $contract" }
}

if ($skill -notmatch '(?s)contract_ambiguity.{0,500}(versioned|版本化)') { throw 'Contract ambiguity must be resolved through a versioned change' }
if ($skill -notmatch '(?s)out_of_scope.{0,500}(versioned|版本化)') { throw 'Out-of-scope findings must require a versioned scope decision' }
if ($skill -notmatch '(?s)code_defect.{0,350}(Worker|worker)') { throw 'Concrete code defects must be routed back to Worker' }
if ($skill -notmatch '(?s)evidence_gap.{0,500}(不得合并|blocks? merge|阻止合并)') { throw 'Unresolved evidence gaps must block merge' }
if ($skill -notmatch '未解决.{0,80}CHANGES_REQUESTED|CHANGES_REQUESTED.{0,80}未解决') { throw 'A resolved historical CHANGES_REQUESTED review must not permanently block merge' }
if ($skill -notmatch '(?s)finding ID.{0,250}(closed|闭合).{0,250}(current HEAD|当前完整 HEAD SHA)') { throw 'Merge eligibility must distinguish resolved historical findings from current review state' }

Write-Output 'PASS orchestrator routing contract'
