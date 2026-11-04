$ErrorActionPreference = 'Stop'; $p = Join-Path (Split-Path -Parent $PSScriptRoot) 'skills/orchestrator/SKILL.md'; $s = Get-Content $p -Raw
foreach ($x in @('brainstorming','writing-plans','task_id','parent_plan','depends_on','gh issue create','task:blocked','Pull Request','项目默认假设目标仓库分支未配置分支保护','明确反证','API `403`','项目默认假设继续','Checks API 返回 `403`','GitHub Actions API','`pull_request`','`head_sha`','当前完整 HEAD SHA','`success`','fail closed')) { if ($s -notmatch [regex]::Escape($x)) { throw "Missing orchestrator contract: $x" } }
Write-Output 'PASS orchestrator contract'
