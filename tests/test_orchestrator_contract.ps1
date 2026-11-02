$ErrorActionPreference = 'Stop'; $p = Join-Path (Split-Path -Parent $PSScriptRoot) 'skills/orchestrator/SKILL.md'; $s = Get-Content $p -Raw
foreach ($x in @('brainstorming','writing-plans','task_id','parent_plan','depends_on','gh issue create','task:blocked','Pull Request','默认项目不配置分支保护策略','若查询因 `403` 被拒绝，才进入例外判断','`403` 本身不能证明无保护','Checks API 返回 `403`','GitHub Actions API','`pull_request`','`head_sha`','当前完整 HEAD SHA','`success`','fail closed')) { if ($s -notmatch [regex]::Escape($x)) { throw "Missing orchestrator contract: $x" } }
Write-Output 'PASS orchestrator contract'
