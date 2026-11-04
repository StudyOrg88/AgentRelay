$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$rules = Get-Content (Join-Path $root 'AGENTS.md') -Raw
$readme = Get-Content (Join-Path $root 'README.md') -Raw
$credentials = Get-Content (Join-Path $root 'docs/credentials.md') -Raw
$examplesPath = Join-Path $root 'examples/issue-routing-scenarios.md'
if (-not (Test-Path $examplesPath)) { throw 'Missing issue routing scenarios' }
$examples = Get-Content $examplesPath -Raw

if ($rules -notmatch 'agentrelay-task' -or $rules -notmatch 'GitHub Issue URL') { throw 'AGENTS.md must make the Issue URL entry the default' }
if ($rules -notmatch '由用户选择设备' -or $rules -notmatch 'worker.{0,100}reviewer.{0,100}orchestrator') { throw 'AGENTS.md must separate device selection from role routing' }
if ($readme -notmatch 'agentrelay-task' -or $readme -notmatch 'Issue URL') { throw 'README must show the Issue URL entry workflow' }
if ($readme -notmatch 'current HEAD|当前 HEAD|当前代码提交' -or $readme -notmatch 'Acceptance v1|验收') { throw 'README must preserve the automatic merge evidence boundary' }
if ($credentials -notmatch "SetEnvironmentVariable\('AGENTRELAY_TOKEN_ROUTER'") { throw 'Credentials must show how to configure the read-only router token' }

$scenarioHeadings = @(
    '待执行任务',
    '已有分支或 PR',
    '范围内代码修复',
    '任务契约含糊',
    '独立审查',
    '自动合并',
    '已完成任务'
)
foreach ($heading in $scenarioHeadings) {
    if ($examples -notmatch [regex]::Escape($heading)) { throw "Missing routing scenario: $heading" }
}
foreach ($field in @('当前状态', '下一角色', '当前会话动作', '证据')) {
    if ($examples -notmatch [regex]::Escape($field)) { throw "Missing scenario field: $field" }
}

if ($examples -match '自动选择设备|按设备负载分配|远程唤醒') { throw 'Examples must not promise device selection or remote wake-up' }
if ($examples -notmatch 'required CI|GitHub Actions' -or $examples -notmatch '自动 squash merge') { throw 'Examples must retain current-HEAD CI and automatic merge gates' }

Write-Output 'PASS issue routing examples'
