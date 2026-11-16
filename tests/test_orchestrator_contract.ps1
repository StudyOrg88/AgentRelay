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
$expectedDecisions = [ordered]@{
    'protection-unverified' = @{ Facts = 'Branch Protection 或适用 Repository Rulesets 未完整读取、适用性不明、权限不足或结果矛盾；Checks API 为空不改变结论'; Outcome = 'PAUSE'; Alternate = 'NOT_ALLOWED' }
    'empty-checks-protection-unverified' = @{ Facts = 'Checks API 返回零条结果，但 Branch Protection 或适用 Rulesets 尚未确认'; Outcome = 'PAUSE'; Alternate = 'NOT_ALLOWED' }
    'authoritative-check-non-success' = @{ Facts = '权威来源明确报告 required check 失败、等待中、运行中或 SHA 不匹配，即使其他来源报告成功'; Outcome = 'BLOCK'; Alternate = 'CANNOT_OVERRIDE' }
    'required-check-result-missing-or-stale' = @{ Facts = 'required check 结果缺失、集合不完整，或仅匹配旧、短、其他 PR 或分支 SHA'; Outcome = 'BLOCK'; Alternate = 'NOT_ALLOWED' }
    'premerge-state-changed' = @{ Facts = '合并前 HEAD、BASE、仓库身份、保护规则或 required-check 集合发生变化'; Outcome = 'REVALIDATE'; Alternate = 'NOT_ALLOWED' }
    'alternate-source-complete' = @{ Facts = '原查询源不可用或结果不完整；替代来源证明同一检查身份、当前完整 HEAD SHA、成功结论和完整检查集合，且无权威非成功结果'; Outcome = 'PASS_THIS_GATE'; Alternate = 'ONLY_WHEN_SOURCE_UNAVAILABLE_OR_INCOMPLETE' }
    'required-checks-all-pass-current-head' = @{ Facts = 'required-check 集合完整已知，且每项均成功并绑定当前完整 PR HEAD SHA'; Outcome = 'PASS_THIS_GATE'; Alternate = 'NOT_NEEDED' }
    'no-required-checks-evidence-complete' = @{ Facts = 'Branch Protection 与适用 Rulesets 已完整确认无 required checks，且本次所需项目验证证据完整'; Outcome = 'PASS_THIS_GATE'; Alternate = 'NOT_NEEDED' }
    'no-required-checks-evidence-incomplete' = @{ Facts = '已确认无 required checks，但本次必要项目验证证据缺失、失败、过期或无法验证'; Outcome = 'BLOCK'; Alternate = 'NOT_ALLOWED' }
    'acceptance-review-gate-incomplete' = @{ Facts = 'Acceptance、独立 Reviewer 或其他现有 Merge Gate 未满足，不因 required checks 通过或缺失而豁免'; Outcome = 'BLOCK'; Alternate = 'NOT_ALLOWED' }
}

function Get-DecisionRows([string]$SkillText) {
    $sectionMatch = [regex]::Match($SkillText, '(?ms)^### 合并门禁决策矩阵\s*\r?\n(?<body>.*?)(?=^### |\z)')
    if (-not $sectionMatch.Success) { throw 'Normative merge-gate decision matrix is missing.' }
    $rows = @()
    foreach ($line in ($sectionMatch.Groups['body'].Value -split "`r?`n")) {
        if ($line -match '^\|\s*(?<priority>P\d+)\s*\|\s*(?<id>[a-z0-9-]+)\s*\|\s*(?<facts>.*?)\s*\|\s*(?<outcome>[A-Z_]+)\s*\|\s*(?<alternate>[A-Z_]+)\s*\|\s*$') {
            $rows += [pscustomobject]@{
                Priority = $Matches.priority
                Id = $Matches.id
                Facts = $Matches.facts
                Outcome = $Matches.outcome
                Alternate = $Matches.alternate
            }
        }
    }
    return $rows
}

function Assert-DecisionMatrixContract([string]$SkillText) {
    if ($SkillText -notmatch [regex]::Escape('本矩阵是唯一规范判定来源；说明性正文不得覆盖或改变矩阵结果')) {
        throw 'The decision matrix must be declared the sole normative source.'
    }
    $matrixRows = @(Get-DecisionRows $SkillText)
    if ($matrixRows.Count -ne $expectedDecisions.Count) {
        throw "Expected $($expectedDecisions.Count) decision rows, found $($matrixRows.Count)."
    }
    $duplicateIds = @($matrixRows | Group-Object Id | Where-Object Count -gt 1)
    if ($duplicateIds.Count -gt 0) { throw "Duplicate decision IDs: $($duplicateIds.Name -join ', ')" }
    $priorities = @($matrixRows | ForEach-Object { [int]$_.Priority.Substring(1) })
    if (($priorities | Sort-Object -Unique).Count -ne $matrixRows.Count) { throw 'Decision priorities must be unique.' }
    foreach ($row in $matrixRows) {
        if (-not $expectedDecisions.Contains($row.Id)) { throw "Unexpected decision ID: $($row.Id)" }
        $expected = $expectedDecisions[$row.Id]
        foreach ($field in @('Facts', 'Outcome', 'Alternate')) {
            if ($row.$field -cne $expected[$field]) {
                throw "Decision '$($row.Id)' has unexpected $field '$($row.$field)'."
            }
        }
    }
    foreach ($id in $expectedDecisions.Keys) {
        if (@($matrixRows | Where-Object Id -CEQ $id).Count -ne 1) { throw "Decision row missing or duplicated: $id" }
    }
    $forbiddenRegressions = @(
        '读取保护规则失败时按没有 required checks 处理',
        '其他来源成功可以覆盖明确失败结果',
        '无 CI 就自动判定通过',
        '旧 SHA 的成功结果可以沿用'
    )
    foreach ($regression in $forbiddenRegressions) {
        if ($SkillText.Contains($regression)) { throw "Forbidden fail-open regression: $regression" }
    }
    foreach ($pattern in @(
        '读取保护规则失败.{0,30}(?:按|视为|当作).{0,20}没有 required checks',
        '权威来源.{0,40}失败.{0,40}(?:仍可|可以).{0,20}(?:通过|合并)',
        '无 CI.{0,30}(?:自动|直接).{0,15}(?:通过|放行)',
        '旧 SHA.{0,30}(?:可|可以).{0,15}(?:沿用|复用|放行)'
    )) {
        if ([regex]::IsMatch($SkillText, $pattern)) { throw "Contradictory fail-open wording detected: $pattern" }
    }
}

Assert-DecisionMatrixContract $s
function Assert-MutationRejected([string]$Name, [string]$MutatedSkill) {
    $rejected = $false
    try { Assert-DecisionMatrixContract $MutatedSkill } catch { $rejected = $true }
    if (-not $rejected) { throw "Decision-matrix validator accepted mutation: $Name" }
}

$completeRow = '| P7 | no-required-checks-evidence-complete | Branch Protection 与适用 Rulesets 已完整确认无 required checks，且本次所需项目验证证据完整 | PASS_THIS_GATE | NOT_NEEDED |'
$staleRow = '| P3 | required-check-result-missing-or-stale | required check 结果缺失、集合不完整，或仅匹配旧、短、其他 PR 或分支 SHA | BLOCK | NOT_ALLOWED |'
Assert-MutationRejected 'missing row' ($s.Replace($completeRow, ''))
Assert-MutationRejected 'duplicate contradictory row' ($s.Replace($completeRow, "$completeRow`n| P9 | no-required-checks-evidence-complete | Branch Protection 与适用 Rulesets 已完整确认无 required checks，且本次所需项目验证证据完整 | BLOCK | NOT_ALLOWED |"))
Assert-MutationRejected 'reversed outcome' ($s.Replace($completeRow, $completeRow.Replace('PASS_THIS_GATE', 'BLOCK')))
Assert-MutationRejected 'stale SHA fail-open outcome' ($s.Replace($staleRow, $staleRow.Replace('BLOCK', 'PASS_THIS_GATE')))
Assert-MutationRejected 'negated normative authority' ($s.Replace('本矩阵是唯一规范判定来源；说明性正文不得覆盖或改变矩阵结果', '本矩阵仅为示例，不是规范判定来源'))
foreach ($regression in @('读取保护规则失败时按没有 required checks 处理', '其他来源成功可以覆盖明确失败结果', '无 CI 就自动判定通过', '旧 SHA 的成功结果可以沿用')) {
    Assert-MutationRejected "forbidden rule: $regression" ($s + "`n$regression")
}
Assert-MutationRejected 'contradictory prose after valid matrix' ($s + "`n权威来源明确报告 required check 失败时仍可继续合并")
Assert-MutationRejected 'negated protection read failure' ($s + "`n读取保护规则失败后按没有 required checks 处理")

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
