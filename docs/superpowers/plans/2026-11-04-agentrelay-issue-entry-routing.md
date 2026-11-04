# AgentRelay Issue Entry and Next-Role Routing 实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 subagent-driven-development（推荐）或 executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让用户只需提供 GitHub Issue 链接，AgentRelay 就能恢复任务状态、判断下一角色与动作，并在角色边界、审查独立性或证据不完整时给出可执行交接，同时保留现有自动合并门槛。

**架构：** 新增 `agentrelay-task` 入口 Skill 读取 GitHub Issue、关联 PR、review、Actions 和仓库规则，并按已批准状态表路由到现有 Worker、Reviewer 或 Orchestrator 流程。若当前会话适合目标角色且证据充分，入口继续执行该角色 Skill；需要独立 Reviewer 会话、人工澄清或证据不足时才停下，给出角色、依据和下一动作。GitHub 是唯一持久化状态，无后台服务。

**技术栈：** Markdown Skills、PowerShell 合约测试、GitHub CLI/API、现有 GitHub Actions 与 Acceptance v1 校验脚本。

**规格：** `docs/superpowers/specs/2026-11-04-agentrelay-issue-entry-routing-design.md`

## 全局约束

- GitHub 是 Issue、PR、review、Actions 和合并记录的唯一事实来源。
- 人工选择设备；系统只判断 `worker`、`reviewer`、`orchestrator` 角色，不选设备、不按负载分配、不远程唤醒会话。
- 用户提供 Issue URL 后，Skill 从 GitHub 持久化记录恢复状态，不依赖隐藏的聊天上下文。
- 入口只做状态恢复与角色判定；随后至多执行一个目标角色流程。需要独立 Reviewer 会话、人工澄清或跨会话交接时停止并给出带证据的交接指令。
- Reviewer 必须在与实现 Worker 分离的新 Codex 会话中运行；不能确认独立性时不得给 PASS。
- 审查发现必须区分范围内代码/证据问题与任务契约歧义；Worker 不得猜测或静默扩大范围。
- 自动 squash merge 保留现有 Acceptance v1、当前 HEAD SHA、review、CI、PR 状态及 fail-closed 门槛。
- 凭据只从授权的本地环境读取，不打印、不提交、不写入 Issue 或 PR。
- GitHub/API/网络失败不得伪造成功状态；保留本地工作并允许后续从 Issue URL 恢复。

## 文件结构

- 创建：`skills/task-entry/SKILL.md` — Issue URL 入口、状态读取、下一角色决策、角色切换与停止规则。
- 修改：`skills/worker/SKILL.md` — 从入口接手新任务或既有 PR、按 reviewer finding 修复、遇契约歧义交回 Orchestrator。
- 修改：`skills/reviewer/SKILL.md` — 结构化 finding 分类、独立会话要求、保持 Acceptance v1 review 字段与 SHA 绑定。
- 修改：`skills/orchestrator/SKILL.md` — 契约问题澄清/版本化处理、入口路由、维持既有自动验收与合并门槛。
- 修改：`AGENTS.md` — 将 `agentrelay-task` 定义为默认 Issue 入口，并记录角色边界。
- 修改：`README.md` — 文档化 Issue 链接用法、角色交接与网络恢复行为。
- 修改：`docs/credentials.md` — 说明入口只读检查所需认证，以及写操作仍使用既有角色凭据。
- 创建：`examples/issue-routing-scenarios.md` — 展示待执行、恢复、审查、修复、契约歧义、合并和完成案例。
- 创建：`tests/test_task_entry_contract.ps1` — 锁定入口 Skill 的状态和路由契约。
- 创建：`tests/test_review_routing_contract.ps1` — 锁定 reviewer finding 结构和独立审查约束。
- 创建：`tests/test_worker_resume_contract.ps1` — 锁定 Worker 恢复、修复和阻塞行为。
- 创建：`tests/test_orchestrator_routing_contract.ps1` — 锁定 Orchestrator 澄清与自动合并门槛。
- 修改：`tests/test_repository_contract.ps1` — 要求新入口与场景示例存在。

## 审查重点（Review Focus）

- **旧/无效 Issue URL、跨仓库 URL 或权限不足**：入口必须在任何写操作前验证 repo/Issue 身份并 fail closed；由 `test_task_entry_contract.ps1` 覆盖。
- **Issue/PR 标签、评论和 review 状态互相矛盾或过期**：入口不能选择较宽松的状态继续执行；报告矛盾并交 Orchestrator；由 `test_task_entry_contract.ps1` 和场景示例覆盖。
- **Reviewer finding 超出 Issue 验收契约或契约本身含糊**：不得自动退回 Worker 盲修；分类为 Orchestrator 澄清；由 `test_review_routing_contract.ps1` 覆盖。
- **同一会话既实现又尝试给 PASS，或已存在分支/PR 却重开任务**：不得违反独立审查或覆盖现有工作；由 Reviewer/Worker 合约测试覆盖。
- **Actions、验收记录或 API 在当前 SHA 上不可验证**：不得自动合并，也不得把失败/未知解释为成功；既有 merge 测试加 `test_orchestrator_routing_contract.ps1` 覆盖。

---

### 任务 1：添加 Issue URL 统一入口 Skill

**文件：**
- 创建：`skills/task-entry/SKILL.md`
- 创建：`tests/test_task_entry_contract.ps1`
- 修改：`docs/credentials.md`

- [ ] **步骤 1：编写失败的入口合约测试**

在 `tests/test_task_entry_contract.ps1` 中检查入口 Skill 文件存在，并包含 Issue URL 校验、GitHub 状态读取、三种角色、交接证据、单次调用单角色、网络失败停止和“不选择设备”规则。

关键断言：

```powershell
foreach ($x in @('GitHub Issue URL','worker','reviewer','orchestrator','task:planned','task:in-progress','task:review','Acceptance v1','fail closed','设备')) {
    if ($skill -notmatch [regex]::Escape($x)) { throw "Missing task-entry contract: $x" }
}
```

- [ ] **步骤 2：运行测试验证失败**

运行：`pwsh -NoProfile -File tests/test_task_entry_contract.ps1`

预期：FAIL，报告缺少 `skills/task-entry/SKILL.md`。

- [ ] **步骤 3：实现 `agentrelay-task` 入口 Skill**

定义 frontmatter 名称 `agentrelay-task`。入口先验证 GitHub repo/Issue URL 与认证，再读取 Issue、依赖、关联 PR、labels/comments、review 和 Actions 状态。按批准规格给出 `next_role`、证据、动作和 `proceed|handoff|blocked|complete` 结果；若当前会话可承担该角色，则继续调用对应既有角色 Skill，且本次最多执行一个目标角色流程。Review 必须满足与实现会话独立的要求；无法确认独立性时停下并要求在新会话用同一 Issue URL 重试。对未知/矛盾状态停止。写操作交给既有角色 Skill 的 token 与流程，不复用只读入口凭据。

- [ ] **步骤 4：补充入口凭据文档并运行测试**

在 `docs/credentials.md` 中说明入口读取所需的最小权限以及入口凭据与角色写凭据的区别；不得把任何真实 token 写入文档。

运行：`pwsh -NoProfile -File tests/test_task_entry_contract.ps1`

预期：PASS，入口的状态、路由和安全边界契约均存在。

- [ ] **步骤 5：Commit**

```bash
git add skills/task-entry/SKILL.md docs/credentials.md tests/test_task_entry_contract.ps1
git commit -m "feat: add AgentRelay issue entry skill"
```

### 任务 2：结构化 Reviewer finding 与独立审查路由

**文件：**
- 修改：`skills/reviewer/SKILL.md`
- 创建：`tests/test_review_routing_contract.ps1`

- [ ] **步骤 1：编写失败的 Reviewer 路由测试**

断言 Reviewer 指引包含独立新会话、禁止实现者自审 PASS、finding 分类，以及每项 finding 的 ID、类别、Issue 验收引用和可复核证据。

```powershell
foreach ($x in @('fresh Codex session','implementation session','code_defect','contract_ambiguity','evidence_gap','out_of_scope','acceptance item','CHANGES_REQUESTED')) {
    if ($skill -notmatch [regex]::Escape($x)) { throw "Missing review routing contract: $x" }
}
```

- [ ] **步骤 2：运行测试验证失败**

运行：`pwsh -NoProfile -File tests/test_review_routing_contract.ps1`

预期：FAIL，当前 Reviewer 协议没有结构化 finding 分类。

- [ ] **步骤 3：更新 Reviewer 评论契约**

保持现有 `agentrelay_review` 的 PASS 字段与 SHA/Acceptance v1 语义不变。对 `CHANGES_REQUESTED`，要求 `findings` 中每项包含 `id`、`category`（`code_defect|contract_ambiguity|evidence_gap|out_of_scope`）、`acceptance_item`（引用或 `null`）、`summary` 和 `evidence`。`code_defect`/`evidence_gap` 仅在确属已批准范围时建议 Worker；`contract_ambiguity`/`out_of_scope` 交 Orchestrator。分类是路由依据，不授予扩大 Issue 范围的权限。

- [ ] **步骤 4：运行 Reviewer 回归测试**

运行：`pwsh -NoProfile -File tests/test_review_routing_contract.ps1` 和 `pwsh -NoProfile -File tests/test_reviewer_contract.ps1`

预期：两个测试均 PASS；旧 reviewer SHA 绑定字段仍完整。

- [ ] **步骤 5：Commit**

```bash
git add skills/reviewer/SKILL.md tests/test_review_routing_contract.ps1
git commit -m "feat: classify AgentRelay review handoffs"
```

### 任务 3：让 Worker 从 Issue 和既有 PR 安全恢复

**文件：**
- 修改：`skills/worker/SKILL.md`
- 创建：`tests/test_worker_resume_contract.ps1`

- [ ] **步骤 1：编写失败的 Worker 恢复测试**

断言 Worker 识别入口交接、继续已有 task branch/PR、按 finding ID 逐项回应、仅修复范围内 finding、契约歧义时停止并交 Orchestrator，且不会覆盖现有分支或 PR。

- [ ] **步骤 2：运行测试验证失败**

运行：`pwsh -NoProfile -File tests/test_worker_resume_contract.ps1`

预期：FAIL，当前 Worker 未消费结构化 finding 路由。

- [ ] **步骤 3：更新 Worker 恢复流程**

新增以 Issue URL/入口 handoff 为恢复入口的说明。若存在分支或 PR，只在确认其 `task_id` 与 Issue 一致后续作；对 `code_defect`/范围内 `evidence_gap` 修复时保留原 PR 并按 finding ID 回应；若 finding 指向 `contract_ambiguity` 或 `out_of_scope`，不改代码，将 Issue 交给 Orchestrator 澄清。Worker 会话不得切换成 Reviewer 或自行出具 review PASS。

- [ ] **步骤 4：运行 Worker 回归测试**

运行：`pwsh -NoProfile -File tests/test_worker_resume_contract.ps1` 和 `pwsh -NoProfile -File tests/test_worker_contract.ps1`

预期：两个测试均 PASS，原有依赖、分支、测试和 PR 契约不退化。

- [ ] **步骤 5：Commit**

```bash
git add skills/worker/SKILL.md tests/test_worker_resume_contract.ps1
git commit -m "feat: resume AgentRelay worker tasks from issues"
```

### 任务 4：接入 Orchestrator 澄清与既有自动合并

**文件：**
- 修改：`skills/orchestrator/SKILL.md`
- 创建：`tests/test_orchestrator_routing_contract.ps1`

- [ ] **步骤 1：编写失败的 Orchestrator 路由测试**

断言 `contract_ambiguity`/`out_of_scope` 指向 Orchestrator，澄清通过显式版本化 Issue/plan 变更留痕，并且原有 Acceptance v1、当前 SHA、required CI 与 squash merge 门槛仍存在。

- [ ] **步骤 2：运行测试验证失败**

运行：`pwsh -NoProfile -File tests/test_orchestrator_routing_contract.ps1`

预期：FAIL，当前 Orchestrator 没有显式消费 reviewer 路由类别。

- [ ] **步骤 3：更新 Orchestrator 路由与合并行为**

新增处理 `contract_ambiguity`/`out_of_scope` finding 的流程：基于已批准计划澄清，创建版本化变更或 follow-up Issue，不静默改写已执行任务。PASS 后调用既有自动验收与合并流程；`code_defect` 未修复、`evidence_gap` 未闭合或门槛未知时不得合并。不得放宽 `scripts/acceptance-policy.ps1` 的 fail-closed 规则。

- [ ] **步骤 4：运行 Orchestrator 与合并回归测试**

运行：`pwsh -NoProfile -File tests/test_orchestrator_routing_contract.ps1`、`pwsh -NoProfile -File tests/test_orchestrator_automation_contract.ps1`、`pwsh -NoProfile -File tests/test_orchestrator_acceptance_contract.ps1` 和 `pwsh -NoProfile -File tests/test_acceptance_policy.ps1`

预期：全部 PASS；Acceptance v1 的当前 HEAD 校验和自动 squash merge 条件保持不变。

- [ ] **步骤 5：Commit**

```bash
git add skills/orchestrator/SKILL.md tests/test_orchestrator_routing_contract.ps1
git commit -m "feat: route AgentRelay contract findings"
```

### 任务 5：把 Issue URL 入口设为项目默认并补充恢复示例

**文件：**
- 修改：`AGENTS.md`
- 修改：`README.md`
- 创建：`examples/issue-routing-scenarios.md`
- 修改：`tests/test_repository_contract.ps1`
- 创建：`tests/test_issue_routing_examples.ps1`

- [ ] **步骤 1：编写失败的项目集成测试**

断言项目规则和 README 以 `agentrelay-task` + Issue URL 为默认入口，入口会返回下一角色与依据；示例覆盖待执行、已有 PR 恢复、范围内修复、契约歧义、独立 Review、自动合并和完成状态。

- [ ] **步骤 2：运行测试验证失败**

运行：`pwsh -NoProfile -File tests/test_issue_routing_examples.ps1`

预期：FAIL，入口说明或场景文件不存在。

- [ ] **步骤 3：更新项目使用文档与场景**

在 `AGENTS.md` 记录默认入口和设备由人选择；README 给出把 Issue URL 交给任意 Codex 的最短用法；示例逐项列出输入状态、入口判定、下一角色、是否在当前会话继续及证据。示例不得暗示远程唤醒、自动设备分配或弱化 merge gate。

- [ ] **步骤 4：运行完整契约回归**

运行：`pwsh -NoProfile -File tests/test_repository_contract.ps1`、`pwsh -NoProfile -File tests/test_task_entry_contract.ps1`、`pwsh -NoProfile -File tests/test_review_routing_contract.ps1`、`pwsh -NoProfile -File tests/test_worker_resume_contract.ps1`、`pwsh -NoProfile -File tests/test_orchestrator_routing_contract.ps1`、`pwsh -NoProfile -File tests/test_issue_routing_examples.ps1`，再运行现有 `tests/test_*.ps1` 全集。

预期：全部 PASS；没有遗漏的 GitHub 专用认证、路由或合并契约。

- [ ] **步骤 5：Commit**

```bash
git add AGENTS.md README.md examples/issue-routing-scenarios.md tests/test_repository_contract.ps1 tests/test_issue_routing_examples.ps1
git commit -m "docs: make AgentRelay issue routing the default"
```

## 执行边界

- 任务 1 建立状态恢复与路由入口；任务 2、3、4 分别定义可消费的 Reviewer、Worker、Orchestrator 角色契约，因此任务 1 先完成，随后 2/3/4 可独立实施。
- 任务 5 依赖前四项的最终命名和路由语义。
- 不修改 Forgejo、GitHub Actions workflow、`scripts/acceptance-policy.ps1` 或 Acceptance v1 schema；本变更只扩展角色路由，不改变平台 API 或自动合并判定。
- 每个任务都按 TDD 先加失败的合约测试，再更新对应 Skill，并以现有 PowerShell 测试模式验证。
