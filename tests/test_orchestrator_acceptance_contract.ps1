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
    'Branch Protection',
    'Repository Rulesets',
    '无 required checks 不能绕过 Reviewer、Acceptance 或现有 Merge Gate',
    'required checks 通过不能替代 Acceptance',
    '必要证据缺失',
    '当前 PR 的完整 HEAD SHA',
    'Windows 与 Linux',
    'PowerShell 7',
    'schema 不匹配',
    '结构化失败报告',
    '不得自动修改验收记录',
    'Issue 标签',
    '停止自动合并'
)) {
    if ($skill -notmatch [regex]::Escape($phrase)) { throw "Missing acceptance compatibility rule: $phrase" }
}

$acceptanceScenarios = @(
    @{ Name = 'no required checks cannot bypass Reviewer'; Rules = @('无 required checks 不能绕过 Reviewer', '独立 Reviewer') },
    @{ Name = 'no required checks cannot bypass Acceptance'; Rules = @('无 required checks 不能绕过 Reviewer、Acceptance 或现有 Merge Gate', '无 required checks 不等于测试通过') },
    @{ Name = 'passing required checks cannot replace Acceptance'; Rules = @('required checks 通过不能替代 Acceptance', '这些仍是彼此独立的合并门槛') },
    @{ Name = 'schema and acceptance record path remain constrained'; Rules = @('acceptance/v1', '历史记录', '不得推断为 v1', 'schema 不匹配', '不得自动修改验收记录') },
    @{ Name = 'review and commit bindings remain constrained'; Rules = @('reviewed_code_commit', 'record_commit_parent', 'git show', 'HEAD SHA', 'BASE') },
    @{ Name = 'missing or unverifiable evidence blocks'; Rules = @('必要证据缺失、失败、过期或无法验证时仍须阻断', '停止自动合并') }
)
foreach ($scenario in $acceptanceScenarios) {
    foreach ($rule in $scenario.Rules) {
        if ($skill -notmatch [regex]::Escape($rule)) {
            throw "Static acceptance scenario '$($scenario.Name)' lacks required rule: $rule"
        }
    }
}

if ($skill -match [regex]::Escape('Checks API 返回 0 条结果即可确认没有 required checks')) {
    throw 'An empty Checks API result must not establish that no checks are required.'
}

$protectionSection = [regex]::Match($skill, '(?s)### 分支保护与 CI 证据(?<body>.*?)(?:\r?\n## |\z)').Groups['body'].Value
if ($protectionSection -notmatch [regex]::Escape('必要证据缺失、失败、过期或无法验证')) {
    throw 'No-required-check path must retain project evidence requirements.'
}
if ($protectionSection -notmatch [regex]::Escape('必须逐项核验完整集合')) {
    throw 'Required checks must be verified individually.'
}
$scriptText = Get-Content -LiteralPath $PSCommandPath -Raw
$windowsDrivePattern = '[A-Z]:' + '\\'
$legacyExecutableName = 'powershell' + '.exe'
if ($scriptText -match $windowsDrivePattern -or $scriptText.Contains($legacyExecutableName)) {
    throw 'The acceptance contract test must not depend on Windows-only paths or legacy executables.'
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
