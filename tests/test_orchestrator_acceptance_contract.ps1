$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$skillPath = Join-Path $root 'skills/orchestrator/SKILL.md'
$policyPath = Join-Path $root 'scripts/acceptance-policy.ps1'
$schemaPath = Join-Path $root 'docs/acceptance-schema-v1.json'
$skill = Get-Content $skillPath -Raw
$policy = Get-Content $policyPath -Raw
$schema = Get-Content $schemaPath -Raw | ConvertFrom-Json

foreach ($phrase in @(
    'acceptance/v1',
    '历史记录',
    '不得推断为 v1',
    'Test-AgentRelayReviewValidity',
    'Test-AgentRelayMergeBlocked',
    'valid = $false',
    'requires_review = $true',
    '表示阻断',
    '表示没有验收项阻断',
    'owner_task',
    'merge_blocking',
    'status',
    'git show',
    'record_commit_parent',
    'reviewed_code_commit',
    'HEAD SHA',
    'API `403`',
    '项目默认假设继续',
    'schema 不匹配',
    '结构化失败报告',
    '不得自动修改验收记录',
    'Issue 标签',
    '停止自动合并'
)) {
    if ($skill -notmatch [regex]::Escape($phrase)) { throw "Missing acceptance compatibility rule: $phrase" }
}

$reviewFunction = [regex]::Match($policy, '(?s)function Test-AgentRelayReviewValidity\s*\{(.*?)\n\}')
$mergeFunction = [regex]::Match($policy, '(?s)function Test-AgentRelayMergeBlocked\s*\{(.*?)\n\}')
if (-not $reviewFunction.Success -or -not $mergeFunction.Success) { throw 'Both policy functions must exist' }
foreach ($parameter in @('Repository','CurrentTask','SchemaPath','ReviewedCodeCommit','PullRequestHead')) {
    if ($reviewFunction.Groups[1].Value -notmatch "\[string\]\`$$parameter") { throw "Review function parameter missing: $parameter" }
}
foreach ($parameter in @('Repository','PullRequestHead','CurrentTask','SchemaPath')) {
    if ($mergeFunction.Groups[1].Value -notmatch "\[string\]\`$$parameter") { throw "Merge function parameter missing: $parameter" }
}
foreach ($field in @('status','merge_blocking','owner_task')) {
    if (@($schema.properties.items.items.required) -notcontains $field) { throw "V1 schema missing item field: $field" }
}
Write-Output 'PASS orchestrator acceptance compatibility contract'
