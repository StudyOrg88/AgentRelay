$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$skill = Get-Content (Join-Path $root 'skills/reviewer/SKILL.md') -Raw

foreach ($contract in @(
    'fresh Codex session',
    'implementation session',
    'code_defect',
    'contract_ambiguity',
    'evidence_gap',
    'out_of_scope',
    'acceptance_item',
    'CHANGES_REQUESTED',
    'reviewed_code_commit',
    'session_id'
)) {
    if ($skill -notmatch [regex]::Escape($contract)) { throw "Missing review routing contract: $contract" }
}

foreach ($field in @('id:', 'category:', 'acceptance_item:', 'summary:', 'evidence:')) {
    if ($skill -notmatch [regex]::Escape($field)) { throw "Missing structured finding field: $field" }
}

if ($skill -notmatch '(?s)code_defect.{0,500}Worker') { throw 'In-scope code defects must hand off to Worker' }
if ($skill -notmatch '(?s)contract_ambiguity.{0,500}Orchestrator') { throw 'Contract ambiguity must hand off to Orchestrator' }

Write-Output 'PASS review routing contract'
