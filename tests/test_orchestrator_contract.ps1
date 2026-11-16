$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$skillPath = Join-Path $root 'skills/orchestrator/SKILL.md'
$s = Get-Content -LiteralPath $skillPath -Raw
foreach ($x in @(
    'brainstorming','writing-plans','task_id','parent_plan','depends_on','gh issue create','task:blocked','Pull Request',
    'Branch Protection','Repository Rulesets','required checks','Checks API 返回 0 条结果不等于目标分支没有 required checks',
    '适用性明确','无法读取','权限不足','查询不完整','结果矛盾','fail closed','当前完整 HEAD SHA',
    '失败、等待、仍在运行、缺少结果','旧 SHA','重新读取 PR 当前 HEAD 和 BASE','pwsh',
    'PowerShell 7','Windows','Linux','Join-Path','$PSScriptRoot'
)) { if ($s -notmatch [regex]::Escape($x)) { throw "Missing orchestrator contract: $x" } }

foreach ($x in @('项目默认假设目标仓库分支未配置分支保护','项目默认假设继续')) {
    if ($s -match [regex]::Escape($x)) { throw "Obsolete fail-open rule remains: $x" }
}

$protectionSection = [regex]::Match($s, '(?s)### 分支保护与 CI 证据(?<body>.*?)(?:\r?\n## |\z)').Groups['body'].Value
if (-not $protectionSection) { throw 'Branch protection and CI section missing.' }
$scenarios = @(
    @{ Name = 'no required checks with complete project evidence'; Required = @('只有成功读取并确认 Branch Protection 与适用 Rulesets 的完整规则后', '明确需要哪些测试、契约、Reviewer、Acceptance 或集成证据', '并核验这些证据确已取得', '不因 Checks API 返回 0 条结果或仓库没有 GitHub Actions workflows 而自动阻断') },
    @{ Name = 'no required checks with missing project evidence'; Required = @('必要证据缺失、失败、过期或无法验证时仍须阻断') },
    @{ Name = 'Branch Protection read failure pauses'; Required = @('无法读取 Branch Protection 或 Rulesets', '暂停合并', '不得把读取失败解释为“没有配置 required checks”') },
    @{ Name = 'Rulesets read failure pauses'; Required = @('无法读取 Branch Protection 或 Rulesets', '所有可能适用的 Repository Rulesets', '暂停合并') },
    @{ Name = 'permission or applicability uncertainty pauses'; Required = @('权限不足', '无法确认 Ruleset 是否适用', '暂停合并') },
    @{ Name = 'contradictory protection results pause'; Required = @('结果矛盾', '暂停合并') },
    @{ Name = 'empty Checks API result is inconclusive'; Required = @('Checks API 返回 0 条结果不等于目标分支没有 required checks', '在规则集合已完整确定之前，空结果不能证明没有 required checks') },
    @{ Name = 'successful required checks match full current SHA'; Required = @('每一项都必须有成功结果', '对应当前 PR 的完整 HEAD SHA') },
    @{ Name = 'failed required check blocks'; Required = @('失败、等待、仍在运行、缺少结果', '均阻断合并') },
    @{ Name = 'in-progress or pending required check blocks'; Required = @('等待、仍在运行', '均阻断合并') },
    @{ Name = 'missing required check result blocks'; Required = @('缺少结果', '均阻断合并') },
    @{ Name = 'stale SHA result blocks'; Required = @('只匹配旧 SHA', '短 SHA', '均阻断合并') },
    @{ Name = 'authorized alternate check source can verify successfully'; Required = @('若 Checks API 返回 `403`，可尝试 GitHub Actions API 或其他权威状态来源', '替代来源必须证明同一 required check 的身份、当前完整 HEAD SHA 和成功结论', '检查集合完整性', '才可继续') },
    @{ Name = 'unverifiable alternate check source blocks'; Required = @('不能证明时 fail closed', '无法确认检查集合完整性', '均阻断合并') },
    @{ Name = 'alternate source cannot override an explicit failed check'; Required = @('若权威来源已经明确返回 required check 失败', '不得使用其他来源的成功结果覆盖该状态', '替代来源仅用于原查询来源不可用或无法取得完整结果的情况') },
    @{ Name = 'pre-merge state is re-read'; Required = @('合并前重新读取', '重新读取 PR 当前 HEAD 和 BASE', '保护规则变化时，以最新完整规则重算门槛') }
)
foreach ($scenario in $scenarios) {
    foreach ($rule in $scenario.Required) {
        if ($protectionSection -notmatch [regex]::Escape($rule)) {
            throw "Static merge-gate scenario '$($scenario.Name)' lacks required rule: $rule"
        }
    }
}
if ($protectionSection.IndexOf('Branch Protection', [StringComparison]::Ordinal) -gt
    $protectionSection.IndexOf('没有 required checks', [StringComparison]::Ordinal)) {
    throw 'No-required-check handling appears before protection-rule discovery.'
}
if ($protectionSection.IndexOf('合并前重新读取', [StringComparison]::Ordinal) -lt 0) {
    throw 'Pre-merge protection and PR state revalidation is missing.'
}
$scriptText = Get-Content -LiteralPath $PSCommandPath -Raw
$windowsDrivePattern = '[A-Z]:' + '\\'
$legacyExecutableName = 'powershell' + '.exe'
if ($scriptText -match $windowsDrivePattern -or $scriptText.Contains($legacyExecutableName)) {
    throw 'The contract test must not depend on Windows-only paths or legacy executables.'
}
Write-Output 'PASS orchestrator contract'
