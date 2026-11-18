# Orchestrator 人工批准保护规则例外实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 subagent-driven-development（推荐）或 executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 在保护规则无法完整读取时，以明确且有范围的人工批准允许继续核验其他合并门槛，并在其余可独立验证门槛通过、无已知检查阻断时只尝试一次正常 GitHub 合并操作。

**架构：** 在 Orchestrator Skill 的规范决策矩阵中分别表达 `protection_config`、`manual_exception`、`required_checks_config` 和一次性 `merge_attempt` 状态；保留现有静态契约测试架构，由测试解析 Skill 实际矩阵并以 mutation 检查约束行为。人工批准只解除 Orchestrator 因规则配置不可读而产生的暂停，不把未知配置改写为无 required checks 或通过，也不绕过 GitHub 原生限制。

**技术栈：** Markdown Skill 规则；PowerShell 7 静态契约测试；Windows 与 Ubuntu aarch64。

**规格：** `docs/superpowers/specs/2026-11-17-orchestrator-manual-protection-exception-design.md`，以及本计划明确的合并操作边界补充：配置未知且人工批准有效时，所有其他 Merge Gate 通过后允许 Merge Bot 使用正常 GitHub 合并操作尝试合并；GitHub 拒绝时停止，不使用 `--admin`、force merge 或其他绕过方式。

## 全局约束

- 执行前置：本计划获用户正式批准后，由 Orchestrator 先创建新的独立策略改进 Issue 和 Acceptance v1 验收契约，再开始实现；不得复用或改写 Issue #1、Issue #12 或 PR #11。
- 实现文件仅限 `skills/orchestrator/SKILL.md`、`tests/test_orchestrator_contract.ps1`、`tests/test_orchestrator_acceptance_contract.ps1`。
- 不修改 Controller、Acceptance schema/policy/实现、Merge Bot、其他 Skill、CI 工作流或已完成 Issue/PR 历史。
- 不验证用户身份或 Owner/Admin 角色；只要求用户明确批准本次人工例外。
- 保护配置读取失败时状态保持 `unknown`；不得称 API/GitHub/管理员已确认无 required checks。
- 状态分别记录：`protection_config = unknown` 表示保护规则不可完整读取；`manual_exception = approved` 表示本 PR 的人工批准有效；`required_checks_config = unknown` 表示完整 required checks 集合仍不能确认。不得将任一 unknown 记录为 `passed` 或 `absent`。
- 人工批准绑定 GitHub host、owner/repository、PR、base、批准时完整 HEAD/BASE SHA；任一变化或 PR 关闭/重开后失效。
- 已知 required check 失败、等待、运行中、缺失或 SHA 不匹配时阻断；人工批准不能覆盖。
- Reviewer、Acceptance、项目验证和其他既有 Merge Gate 独立有效。
- 只有人工例外有效、所有可独立验证的其他 Merge Gate 均通过、所有已明确识别的 required checks 均无阻断状态时，才允许一次正常 GitHub 合并尝试；保护配置和完整 required-check 集合继续为 `unknown`，由 GitHub 原生规则执行最终限制。
- 调用合并前先在当前 PR 创建持久化审计评论，使用 `agentrelay_merge_attempt` YAML 记录唯一 `attempt_id`、`status: started`、批准范围、当前完整 HEAD/BASE 和时间及时区；确认评论可从 GitHub 重新读取后才可调用正常合并操作，并必须使用完整当前 HEAD SHA 前置条件（例如 `--match-head-commit <SHA>`）。如果现有 Merge Bot/CLI 不支持该前置条件，必须暂停，禁止退化为不带 SHA 锁定的合并命令。评论无法确认写入或读回时也禁止调用。同一批准记录不得再次尝试。
- 恢复时先读取 PR 审计评论与 GitHub 当前 PR 状态；`status: started` 且 PR 已合并时核验 `merged = true` 和完整 `merge_commit_sha`，并将结果补记；PR 仍开放或执行结果无法确认时暂停，不得重试。GitHub 明确拒绝时停止并将拒绝事实写入该记录。只有前次结果已明确、拒绝原因已处理且全部门槛重新核验通过，才可申请新的批准并建立新的 `attempt_id`。任何仍为 `started` 且结果不明的尝试都不可由新批准重置；已知检查失败仍阻断。
- 正常合并尝试不得包含 `--admin`、force merge 或其他绕过选项；GitHub 拒绝即停止并报告。
- 测试是 Skill 静态契约测试，不声称模拟 Merge Bot 运行时行为。
- Windows 10 / PowerShell 7 与 Ubuntu aarch64 / PowerShell 7 分别实际运行两个定向脚本和全部 17 个合约脚本，并执行 `git diff --check`；未实际运行的平台必须标记未验证。

## 文件职责

- 修改：`skills/orchestrator/SKILL.md`，定义用户询问、批准记录、例外状态转移、决策矩阵及正常 GitHub 合并尝试边界。
- 修改：`tests/test_orchestrator_contract.ps1`，解析 Skill 中的规范决策矩阵，验证场景 ID、条件、结果、人工批准作用域/失效条件及反向 mutation。
- 修改：`tests/test_orchestrator_acceptance_contract.ps1`，验证人工例外不能替代 Reviewer、Acceptance、证据和既有提交绑定。
- 交付时另附该新任务所需的独立 Acceptance v1 记录；它是任务验收材料，不扩展上述 Skill/测试实现文件范围。

## 审查重点（Review Focus）

- 批准是否只豁免规则配置可读性证明，而意外将 `unknown` 变成 `absent` 或 `passed`；测试须分别断言配置未知、批准有效和 required-check 集合未知。
- HEAD、BASE、PR、base 或仓库身份变化后是否仍沿用批准；测试须验证作用域字段完整及变化即失效。
- 权威来源已报告失败/等待/运行中/旧 SHA 时，批准或另一绿色来源是否覆盖；测试须保持这些矩阵行为为阻断。
- 正常合并操作边界是否被误读为绕过保护或重复执行；测试须要求持久化 attempt、强制 HEAD 前置条件（能力不支持即暂停）、未解决 started 状态禁止重试及 GitHub 拒绝即停止，并拒绝 `--admin`/force merge 规则。
- 人工例外是否弱化 Reviewer、Acceptance 或项目验证；Acceptance 契约测试须逐项验证这些门槛仍独立阻断。

---

### 任务 1：保护规则不可读时的人工批准例外

**文件：**
- 修改：`skills/orchestrator/SKILL.md`
- 修改：`tests/test_orchestrator_contract.ps1`
- 修改：`tests/test_orchestrator_acceptance_contract.ps1`
- 交付证据：新任务专属 Acceptance v1 记录（按 Issue 契约生成；不修改 Acceptance schema/policy）

- [ ] **步骤 1：为人工例外决策矩阵和边界编写失败的静态契约测试**

在 `test_orchestrator_contract.ps1` 中解析 `SKILL.md` 的实际规范决策矩阵，不另造仅以 Skill 关键词存在为准的测试。解析每行的 Scenario ID、条件和结果；检查下列规定 ID 唯一且完整、条件和结果精确匹配：

```powershell
@{ Id = 'protection-unverified-no-approval'; Outcome = 'PAUSE'; Facts = '保护规则读取不完整且没有明确人工批准' }
@{ Id = 'manual-approval-scoped'; Outcome = 'CONTINUE_OTHER_GATES'; Facts = '明确人工批准且 repository、base、PR、HEAD 和 BASE 均匹配；protection_config = unknown；manual_exception = approved；required_checks_config = unknown' }
@{ Id = 'manual-approval-scope-changed'; Outcome = 'PAUSE'; Facts = 'repository、base、PR、HEAD、BASE 任一变化或 PR 关闭/重开' }
@{ Id = 'manual-approval-known-check-failure'; Outcome = 'BLOCK'; Facts = '已知 required check 失败、等待中、运行中、缺失或 SHA 不匹配' }
@{ Id = 'manual-approval-other-gates-incomplete'; Outcome = 'BLOCK'; Facts = '人工批准有效但 Reviewer、Acceptance、项目验证或其他 Merge Gate 未满足' }
@{ Id = 'manual-approval-other-gates-complete'; Outcome = 'ALLOW_NORMAL_GITHUB_MERGE_ATTEMPT'; Facts = '人工例外有效；所有可独立验证的其他 Merge Gate 均通过；所有已明确识别的 required checks 均无阻断状态；protection_config 和 required_checks_config 保持 unknown；attempt 记录已持久化并读回；完整 HEAD SHA 锁定能力可用；仅允许一次正常 GitHub 合并操作' }
@{ Id = 'github-rejects-normal-merge'; Outcome = 'STOP'; Facts = 'GitHub 拒绝正常合并操作；记录 status = rejected 和拒绝原因' }
@{ Id = 'merge-attempt-recorded-before-call'; Outcome = 'RECORD_THEN_ATTEMPT'; Facts = '先持久化一次性 started 记录、批准范围、当前完整 HEAD/BASE 和时间，并确认读回；再以完整 HEAD 为前置条件调用' }
@{ Id = 'merge-attempt-recovery-no-retry'; Outcome = 'STOP_OR_VERIFY_MERGED'; Facts = '恢复时先读取记录和 PR；已合并则核验完整 merge_commit_sha 并记录 status = merged；PR 仍开放或结果不明时保持 started、不得重试，且新批准不能重置' }
@{ Id = 'merge-attempt-explicit-rejection-resolved'; Outcome = 'REQUIRE_NEW_APPROVAL'; Facts = '前次拒绝结果明确、拒绝原因已处理且所有门槛重新通过后才可新批准并创建新 attempt_id' }
@{ Id = 'merge-head-lock-unavailable'; Outcome = 'PAUSE'; Facts = 'Merge Bot/CLI 不支持用完整当前 HEAD SHA 锁定合并操作' }
```

测试必须从 Skill 实际读取矩阵并检查规定 ID 唯一且完整、每个条件与结果精确匹配；删除矩阵行、反转结果、添加重复/矛盾行或追加与矩阵冲突的规范性规则时必须失败。另验证三个状态独立表达：`protection_config = unknown`、`manual_exception = approved`、`required_checks_config = unknown`；不得断言 unknown 等于通过/不存在。断言用户询问和审计字段、无需身份/角色验证、已知失败不能被批准覆盖、调用前 PR 审计评论必须持久化且成功读回、同一批准禁止重试、完整 HEAD 前置条件为强制要求且不支持时暂停、未解决 `started` 尝试不得被新批准重置、仅在前次结果明确且拒绝原因已处理后才可新建尝试，以及禁止 `--admin`/force merge。Acceptance 合约测试增加独立场景，确保人工批准不能替代 Reviewer、Acceptance、项目验证或现有 HEAD/BASE 与记录绑定要求。

- [ ] **步骤 2：运行两个定向测试确认新增断言按预期失败**

运行：

```powershell
pwsh -NoProfile -File tests/test_orchestrator_contract.ps1
pwsh -NoProfile -File tests/test_orchestrator_acceptance_contract.ps1
```

预期：至少新增的决策/作用域/合并边界断言失败，现有测试执行环境正常；如失败来自测试脚本语法或环境，先修正测试本身再继续。

- [ ] **步骤 3：在 Skill 中实现人工批准规则**

更新 `skills/orchestrator/SKILL.md` 的分支保护与 CI 决策矩阵及说明：不可完整读取时暂停并用大白话询问；仅明确批准进入作用域绑定的 `MANUAL_PROTECTION_EXCEPTION`；记录仓库、base、PR URL/编号、批准时完整 HEAD/BASE、读取错误、批准原文、时间及时区和证据引用；身份及角色不验证。分别保持 `protection_config = unknown`、`manual_exception = approved`、`required_checks_config = unknown`，不得将 unknown 记为 `passed` 或 `absent`。批准失效、用户不明确、已有 required check 非成功、其他 Merge Gate 未通过时分别暂停/阻断。仅当例外有效、所有可独立验证的其他门槛通过且所有已明确识别的 checks 均无阻断状态时，允许一次正常 GitHub 合并尝试。调用前在 PR 写入并确认读回包含唯一 attempt ID、批准范围、HEAD/BASE、时间和 `status: started` 的持久审计评论；必须以完整 HEAD 为前置条件调用，若现有 Merge Bot/CLI 不支持则暂停，不能降级。恢复时先核验 PR 状态和评论：已合并则验证 `merged = true` 与完整 merge SHA；仍开放或结果不明则不得重试；明确拒绝则记录并停止。只有前次结果明确、拒绝原因已处理且全部门槛重新核验通过后，才可重新取得批准并新建记录；新批准不能重置未解决的 `started` 状态。不得使用 `--admin`、force merge 或其他绕过。更新的决策矩阵仍是唯一规范判定来源，且不得改变既有无人工例外路径的行为。

- [ ] **步骤 4：运行两个定向测试确认通过**

运行：

```powershell
pwsh -NoProfile -File tests/test_orchestrator_contract.ps1
pwsh -NoProfile -File tests/test_orchestrator_acceptance_contract.ps1
```

预期：两者均输出对应 `PASS` 行并以退出码 `0` 结束；mutation 场景均拒绝退化规则。

- [ ] **步骤 5：在 Windows PowerShell 7 运行全部合约脚本及差异检查**

运行两个定向脚本，并逐个运行 `tests/` 下全部 17 个 `.ps1` 文件（不要使用会被 PowerShell 字面传参的 `tests/*.ps1` 单次调用方式）；循环开始时断言发现的脚本数恰为 `17`，否则立即失败。Windows 与 Ubuntu 使用相同的脚本发现/执行清单；记录 Windows 版本、PowerShell 版本、每条命令和逐脚本退出码。然后运行：

```powershell
$failures = @()
$scripts = @(Get-ChildItem -LiteralPath tests -Filter '*.ps1' | Sort-Object Name)
if ($scripts.Count -ne 17) { throw "Expected 17 PowerShell contract scripts, found $($scripts.Count)." }
$scripts | ForEach-Object {
    & pwsh -NoProfile -File $_.FullName
    $exitCode = $LASTEXITCODE
    '{0}: exit {1}' -f $_.Name, $exitCode
    if ($exitCode -ne 0) { $failures += $_.Name }
}
if ($failures.Count -gt 0) { throw "Failed scripts: $($failures -join ', ')" }

git diff --check
```

预期：两个定向脚本和全部 17 个脚本通过，`git diff --check` 无输出且退出码为 `0`；失败必须原样报告，不扩大范围。

- [ ] **步骤 6：在 Ubuntu aarch64 / PowerShell 7 重新运行全部合约脚本及差异检查**

在包含本任务准确 HEAD 的 Ubuntu worktree 中记录 `uname -a`、`pwsh --version`、干净/预期工作区状态和完整 HEAD SHA；使用步骤 5 的同一脚本发现、数量断言及执行循环，逐个运行两个定向脚本和 `tests/` 下全部 17 个 `.ps1` 文件，再运行 `git diff --check`。记录每个退出码，不以 Windows 结果代替 Linux 证据；无法实际运行时明确标记未验证。

逐脚本执行与记录使用任务 1 步骤 5 的 PowerShell 7 循环；Linux 环境另运行 `uname -a` 和 `pwsh --version` 记录平台信息。

- [ ] **步骤 7：提交初始 Acceptance v1 记录**

在 Reviewer 审查前，基于已提交代码生成规范 Acceptance v1 文件，准确标记已验证项为 `passed`、Linux/Reviewer 等未完成项为 `pending`。设置 `reviewed_code_commit` 为实际代码提交、`acceptance_base_commit` 为收集本版证据时的 base、`record_commit_parent` 为本 Acceptance 文件提交的真实第一父提交。按 schema 验证并提交该记录；不得伪造尚未取得的证据。

- [ ] **步骤 8：在最终代码 HEAD 上请求独立 Reviewer 审查**

由与实现 Worker 分离的新 Reviewer 会话审查新 Issue 定义的验收项，绑定实现代码的完整 HEAD SHA。Reviewer 只判断 Issue 范围内契约；如发现问题，按 finding 分类返回 Worker 或 Orchestrator，不得扩大验收范围。若之后仅新增 Acceptance 记录，按仓库规则确认其不改变已审查代码；任何代码或 Skill/测试变化均须对新代码 HEAD 重新审查。

- [ ] **步骤 9：根据 Reviewer 结果更新并验证 Acceptance v1 记录**

仅在 Reviewer 结论可接受后，更新 Acceptance v1 状态和证据；Linux 未验证时对应项必须保持 `pending`。更新后将 `record_commit_parent` 改为本次更新提交的真实第一父提交，保留 `reviewed_code_commit` 绑定代码提交。使用仓库现有 Acceptance schema/policy 验证，不修改 schema/policy；Reviewer 或 Acceptance 不通过时不得进入合并准备。

- [ ] **步骤 10：核对最终 Diff、范围和交付证据**

确认 Skill/测试代码差异仅涉及列出的三个批准文件；新任务 Acceptance v1 记录逐项对应 Issue 验收标准，且记录的真实提交父关系、BASE/HEAD 绑定和 Reviewer 要求未被本任务修改或绕过。对照批准规格逐条确认所有验收项及上述正常合并尝试补充均有对应矩阵规则和测试。

- [ ] **步骤 11：提交任务范围内的实现和证据**

在新任务专属 `task/<task_id>` 分支上，仅暂存三个批准实现文件及该任务要求的 Acceptance v1 记录，提交已验证结果；不修改、不重开、不复用 Issue #12、PR #11 或 Issue #1 的历史记录。

