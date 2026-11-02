$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$reviewer = Get-Content (Join-Path $root 'skills/reviewer/SKILL.md') -Raw
$issue = Get-Content (Join-Path $root '.github/ISSUE_TEMPLATE/task.yml') -Raw
$pr = Get-Content (Join-Path $root '.github/pull_request_template.md') -Raw

foreach ($field in @('status','merge_blocking','evidence_type','accepted_evidence','rejected_evidence','owner_task')) {
    if ($issue -notmatch [regex]::Escape($field)) { throw "Issue template missing acceptance field $field" }
}
foreach ($field in @('reviewed_code_commit','acceptance_base_commit','record_commit_parent')) {
    if ($pr -notmatch [regex]::Escape($field)) { throw "PR template missing acceptance field $field" }
}
if ($reviewer -notmatch '不得增加.*Issue.*验收') { throw 'Reviewer must not add acceptance requirements beyond the Issue contract' }
Write-Output 'PASS acceptance authority'
