# AgentRelay Codex Session Orchestration Implementation Plan

> **面向 AI 代理的工作者：** 批准计划后由当前会话亲自执行；不创建 GitHub Issues，不派生子代理，不提交或推送。

**目标：** 让 AgentRelay Orchestrator 在 Codex Desktop 中通过原生会话工具把目标 clone 中的 Issue 交给独立 Worker/Reviewer Chat，并沿用可恢复状态和现有 fail-closed 验收/合并门禁。

**架构：** Codex Desktop 负责项目发现、创建和读取 Chat；AgentRelay Skills 负责角色路由、状态恢复和审查契约。现有 Controller 只做 GitHub 证据、Acceptance 与安全合并逻辑的适配，不连接或模拟 Desktop 会话工具。

**技术栈：** .NET 10、GitHub CLI/API、PowerShell 7 合约测试、Codex Desktop Skills。

**规格：** `docs/superpowers/specs/2026-11-14-agentrelay-codex-session-orchestration.md`

## 文件地图

- `src/AgentRelay.Controller/GitHubStateReader.cs`：容许 Issue-only 状态；解析 PR base SHA/ref、仓库 default branch 和 merge commit SHA；解析 Reviewer head/base SHA。
- `src/AgentRelay.Controller/MergeCoordinator.cs`：验证 target repo identity、可信 default-branch base、head/base-bound Acceptance/Review，并返回合并后 SHA。
- `src/AgentRelay.Controller/GhMergeGitHubClient.cs`：沿用现有 Merge Bot 调用并附加 expected-head 条件。
- `src/AgentRelay.Controller/ControllerCli.cs`、`Program.cs`、`ControllerComposition.cs`：提供不依赖设备清单的 merge-only CLI 接线。
- `skills/orchestrator/SKILL.md`：定义 Desktop session handoff、Superpowers 能力前提、fetch、状态恢复和 merge CLI 调用。
- `skills/reviewer/SKILL.md`：委托 Superpowers 创建隔离 Worktree 并执行只读审查；只规定 AgentRelay 报告字段和目标 SHA/工作区验证契约。
- `scripts/agentrelay-handoff.ps1`：先校验 GitHub.com Issue/repo identity，再在目标 clone Git common dir 下读取、校验并原子保存每 clone 唯一 handoff JSON，以及解析 workspace identity/evidence。
- `tests/controller/*Tests.cs`：覆盖 PR 元数据、可信 Acceptance 来源、条件合并和 merge CLI。
- `tests/test_codex_session_handoff_state.ps1`、`tests/test_codex_session_orchestration_contract.ps1`：覆盖 handoff 状态 helper 与 Skills 契约。
- `README.md`：更新主路径说明；仅在现有说明冲突时调整 `docs/device-agent-installation.md`。

## 全局约束

- 每个目标 clone Git common dir 一个活动 Issue；不提供跨 clone/repository 全局锁，用户负责避免多个槽并行运行。
- Worker 与 Reviewer 必须是独立 Thread 和独立于目标基础/Orchestrator 工作区的实际工作区，且两角色彼此独立；Superpowers 可复用已有隔离环境或按需创建，不保证每个 Chat 新建 Worktree。任何角色都不得原地回退；无法隔离时 BLOCKED。
- Acceptance 记录和提交图针对 PR HEAD；可信 Acceptance v1 schema/policy 针对经过验证的 PR base SHA。
- 合并前要求服务端验证预期 PR HEAD；不支持该条件时失败关闭。
- 保留现有 Acceptance v1、Reviewer、required-check、PR 状态及 Merge Bot 门禁。
- 不新增服务、队列、数据库、远程执行代理或设备注册表；旧 .NET/SSH 执行路径保留为 legacy/experimental。
- 不创建 GitHub Issues，不提交、不推送、不操作真实 PR。
- Superpowers 是 Worker/Reviewer 必需依赖。其可用性、using-git-worktrees 环境检测/复用/创建行为、操作授权、实际工作区身份及 SHA 可验证性必须实测；不得假定每次都会创建新 Worktree。Worker 与 Reviewer 的共享工作区回退一律拒绝；隔离建立不得修改基础项目，包括编辑或提交 `.gitignore`，无法无污染隔离则 BLOCKED。未通过前不得宣称完整 MVP 可用。
- MVP 仅支持 GitHub.com Issue，repo identity 为规范化 `github.com/owner/repository`；GHES fail closed。AgentRelay 是工具/源码仓库，不默认作为任务目标。用户为每个 Issue 选择与 Issue URL 匹配的 Orchestrator 项目；handoff 位于经验证目标 clone 的 Git common dir，Acceptance verifier 只用该 clone 的 Git object database。Worker、Reviewer 项目也必须匹配同一 GitHub.com 仓库。Orchestrator/Controller 执行主机必须能直接访问 AgentRelay Controller 项目和目标 clone；Worker/Reviewer 可远端运行。若目标 clone 只在远端，Phase 1 暂停，不实现远程 Controller。merge-only CLI 从 AgentRelay 工具仓库运行并显式接收目标 repo 路径。handoff origin mismatch 必须在派生状态路径和任何状态读写前失败。

## 审查重点（Review Focus）

- Worker 创建 PR 前可以没有 PR 元数据；Issue-only 状态必须允许正常分析和 Worker 路由。PR 存在后，base SHA/ref/default branch 元数据缺失或格式错误时，阻止 Acceptance 和 merge；由 GitHub state reader 测试覆盖。
- Worker 完成后缺少预期 PR 不得重复分派；必须暂停为 `worker_pr_missing`，并测试成功/无改动/失败报告与 PR 缺失的路由。
- Orchestrator、Worker、Reviewer 项目的 origin 必须规范化为大小写不敏感的 `github.com/owner/repository` 并与 Issue URL 一致；GHES 拒绝。Orchestrator mismatch 在状态路径派生/读写前拒绝，角色项目 mismatch 在任务文件读写前拒绝。AgentRelay 源码仓库与目标仓库可不同但不能混用对象库。
- Controller 必须从 AgentRelay 工具项目可访问的位置启动，并显式接收同一执行主机上的目标 clone 路径；远端 Worker/Reviewer 不意味着远端 Controller。
- PR base 必须是 GitHub repository 当前 `default_branch`；Reviewer PASS 同时绑定 PR head/base SHA，任一变化都使证据过期。
- PR 修改 schema/policy 时不能用 PR HEAD 规则验收自己；由 PR base/PR head schema 不同的测试覆盖。
- merge CLI 不支持 `--match-head-commit` 或预期 SHA 不匹配时不得退回无条件合并；由参数契约和失败路径测试覆盖。
- `create_thread` 返回不确定或主 Chat 中断时不得重复创建；由 handoff 状态恢复契约覆盖。
- 验证 Orchestrator 项目 origin 后才可定位/读写其 Git common-dir handoff；新建与恢复均绑定已验证的同一个 repo/project。Worker 在身份验证后仍必须读取仓库指令；Reviewer 只读所需资料。由状态 helper/Skill 契约测试覆盖身份错配不产生文件读写。
- Worker 与 Reviewer 均调用 Superpowers `using-git-worktrees`；允许检测/复用 Codex Desktop 已提供的隔离工作区或按需创建 Worktree，但不允许任一角色回退到目标基础工作区，也不得为了创建 Worktree 修改基础项目 `.gitignore`。两角色均独立于 base/Orchestrator workspace 且彼此独立。同一 host 上物理规范化路径相同不得 PASS，不同 host 的同字符串路径是不同身份，共享 Git common dir 的独立 Worktree 不得误拒；由 Worker/Reviewer Skill 合约与桌面端手动验收覆盖。
- Reviewer 准备阶段必须验证 origin、获取新鲜 GitHub PR HEAD/BASE、定位隔离 workspace 到精确 HEAD、验证 BASE commit object 与 clean 状态；完成后才进入只读审查，范围精确为 BASE SHA → HEAD SHA。审查开始后 HEAD、index、branch 和 workspace files 均不得修改。
- Worker 交付权限为按任务创建分支、commit、push 和创建/更新 PR；明确禁止 merge。成功报告为结构化 JSON，至少含 PR URL、branch、完整 commit SHA、tests/results、change summary、Acceptance evidence 和 workspace evidence。
- Worker/Reviewer workspace 报告保留原始可核查字段；隔离判断只用 `(host_id, canonical_workspace_path)`，git dir/common dir 仅作辅助证据。PR BASE 对象以 `git cat-file -e <base-sha>^{commit}` 验证可用，不把 BASE 当第二个 checkout。
- 将 expected-head merge 的残余风险写入规格/README：PR BASE 并未被原子锁定，只在 merge 前重读，既有分支保护/Merge Queue policy 仍然适用。

---

### 任务 1：读取 Issue 与 PR 阶段元数据

**文件：**
- 修改：`src/AgentRelay.Controller/GitHubStateReader.cs`
- 修改：`tests/controller/GitHubStateReaderTests.cs`
- 修改：`tests/core/TaskLifecycleTests.cs`

- [ ] **步骤 1：增加 base SHA 合约测试**

覆盖初始 Issue 无 PR 时 state read 成功且 `task:planned` 仍可路由 Worker；PR 存在时暴露完整 `base.sha`、`base.ref`、repository `default_branch`；base SHA/ref/default branch 错误时失败关闭；merged PR 缺失/错误 `merge_commit_sha` 时不得表示验证成功。已有 Worker Thread 的活跃/终止/无 PR 路由状态由任务 5 的 handoff/Skill 合约测试覆盖，避免把 Desktop thread state 错放入 `TaskLifecycle`。

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~GitHubStateReader` 和 `dotnet test tests/core/AgentRelay.Core.Tests.csproj --filter FullyQualifiedName~TaskLifecycle`

预期：Issue-only state reader 测试保持通过，`task:planned` 无 PR 仍路由 Worker；PR fixture 新增 base/default-branch 元数据测试因快照字段/校验不存在而失败。更新内嵌 PR JSON fixture 添加 `base.sha` 和 `base.ref`。

- [ ] **步骤 3：增加 base SHA 解析**

在 `GitHubStateSnapshot` 增加 `PullRequestBaseSha`、`PullRequestBaseRef`、`RepositoryDefaultBranch` 和 `MergeCommitSha` 属性。`GhGitHubClient.ReadIssueStateAsync` 在有关联 PR 时读取 repository `default_branch`；Compose 解析 PR `base.sha`、`base.ref`、`merge_commit_sha`。Issue-only 时 PR 专属字段保持 null 且不失败；PR present 时 base SHA 必须为完整 40 位小写十六进制，base ref/default branch 必须是非空安全 Git ref；merged PR 必须提供完整 merge commit SHA。

- [ ] **步骤 4：运行 GitHub state reader 测试**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~GitHubStateReader` 和 `dotnet test tests/core/AgentRelay.Core.Tests.csproj --filter FullyQualifiedName~TaskLifecycle`

预期：全部 GitHub state reader 与 TaskLifecycle 测试通过；初始 Issue-only 正常进入 Worker，PR-present 元数据错误和 merged 无 merge SHA 被拒绝。Worker terminal/no-PR 不重复创建 Worker 的路由契约在任务 5 验证。

### 任务 2：从可信 base 验证 Acceptance v1

**文件：**
- 修改：`src/AgentRelay.Controller/MergeCoordinator.cs`
- 修改：`src/AgentRelay.Controller/GitHubStateReader.cs`
- 修改：`src/AgentRelay.Core/TaskLifecycle.cs`
- 修改：`tests/controller/MergeCoordinatorTests.cs`
- 修改：`tests/controller/GitHubStateReaderTests.cs`
- 修改：`tests/core/TaskLifecycleTests.cs`
- 参考：`docs/acceptance-schema-v1.json`
- 参考：`scripts/acceptance-policy.ps1`

- [ ] **步骤 1：增加可信 schema 来源测试**

将 `AcceptanceRepository` fixture 拆成 PR-base commit、reviewed-code commit 和 Acceptance-record commit，并给临时 repo 配置可识别的 GitHub.com `origin`。测试：HTTPS/SSH GitHub.com remote 归一化一致且忽略 owner/repo 大小写；GHES host 拒绝；目标 repo origin 与 Issue repo 不匹配时拒绝，即使 AgentRelay 源码仓库 origin 正确也不得使用其对象库；base ref 不是 repository `default_branch` 拒绝；base commit 含受支持 schema 且 PR HEAD 修改 schema/policy 时拒绝；base SHA/对象缺失拒绝；reviewed base SHA 和当前 PR base 不同拒绝；PR HEAD 未变但 PR base 变化时 Acceptance 与 `TaskLifecycle` 都必须失效/重新路由 Reviewer；默认分支 base 下 schema/policy 未变化且 head/base review 均匹配时有效记录通过。

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~MergeCoordinator`

预期：target repo identity、allowed base、base-bound Reviewer 和 schema/policy fail-closed 新增测试当前失败，证明现有实现只绑定 PR HEAD 且不验证 Issue repo 与 verifier repo 的身份。

- [ ] **步骤 3：将验证规则绑定到 PR base**

验证器先要求目标仓库 `origin` owner/repo 与 Issue URL 一致、PR base ref 等于 GitHub `default_branch`、PR base/head 完整 SHA 对象存在；再从 `snapshot.PullRequestBaseSha` 读取 `docs/acceptance-schema-v1.json`，从 PR HEAD 读取 Acceptance record 并检查代码 ancestry。对 schema/policy 路径比较 base..head，有变化时在没有机器可验证审批字段的 MVP 中 fail closed。扩展 `MergeReviewEvidence` 与 `ReviewPass` 解析/模型，要求 `reviewed_head_sha == reviewed_code_commit == current head` 和 `reviewed_base_sha == current PR base`；`TaskLifecycle` 在 head 或 base 不匹配时重走 Reviewer。`VerifiedMergeGates` 显式保留 base 与 head SHA。

- [ ] **步骤 4：运行验收测试**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~MergeCoordinator`

预期：repo identity/default base、schema/policy 变更、base/head 缺失或不一致均阻止 merge；仅完整、同仓库、默认分支 base 且 Review head/base 同时匹配时可通过。

### 任务 3：为合并添加 expected-head 前置条件

**文件：**
- 修改：`src/AgentRelay.Controller/GhMergeGitHubClient.cs`
- 修改：`tests/controller/GhMergeGitHubClientTests.cs`
- 修改：`tests/controller/MergeCoordinatorTests.cs`（如接口签名变动影响 Fake）

- [ ] **步骤 1：增加 merge 参数测试**

断言 `gh pr merge` 参数包含 `--match-head-commit` 和快照的完整 `CurrentHeadSha`；PR HEAD 缺失或格式错误时，在调用进程前失败。

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~GhMergeGitHubClient`

预期：期望参数数组包含 expected-head 条件的新增测试失败。

- [ ] **步骤 3：向现有 gh 命令传递 expected head**

保持当前 `gh pr merge ... --squash` 路径，在 `GhMergeGitHubClient` 增加 `--match-head-commit <CurrentHeadSha>`；不支持该选项时由 gh 命令失败并沿现有 unknown-result reconciliation 路径处理，不进行无条件重试或降级。Coordinator 在发起 merge 前还必须确认 fresh PR base SHA 与 Acceptance/Reviewer 使用的 base SHA 相同。

- [ ] **步骤 4：运行 merge client 与 coordinator 测试**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter 'FullyQualifiedName~GhMergeGitHubClient|FullyQualifiedName~MergeCoordinator'`

预期：参数、当前 HEAD gate、冲突/不确定结果 reread 和不重复 merge 测试全部通过。

### 任务 4：提供复用现有 merge gates 的一次性 CLI 命令

**文件：**
- 修改：`src/AgentRelay.Controller/ControllerCli.cs`
- 修改：`src/AgentRelay.Controller/Program.cs`
- 修改：`src/AgentRelay.Controller/ControllerComposition.cs`
- 修改：`src/AgentRelay.Controller/MergeCoordinator.cs`
- 修改：`tests/controller/ControllerCliTests.cs`
- 修改：`tests/controller/ControllerCompositionTests.cs`
- 修改：`tests/controller/MergeCoordinatorTests.cs`

- [ ] **步骤 1：测试 `merge` 子命令和 expected-head 检查**

新增 `merge <issue-url> <expected-head-sha> --repository <target-repo-path>` 契约：拒绝非 canonical GitHub.com Issue URL、非完整 SHA、缺少/错误参数；GHES host 拒绝；指定路径必须是当前执行主机可访问的 Git repo，且 canonical origin 必须匹配 Issue URL；不可访问路径或身份不匹配时不调用 GitHub merge/client。GitHub 当前 HEAD 与 expected SHA 不同则不进入 merge gates、不调用 merge client；完全匹配时调用现有 `MergeCoordinator` 并返回 merged/blocked/unknown 的非敏感结果。Merge Queue 入队但 PR 尚未 merged 时返回 unknown/not-complete，不报告 merged。确认合并后结果必须携带 GitHub 复读得到的 `merge_commit_sha`，否则不得报告 merged。该命令独立于旧 device registry/Agent 调度配置。

- [ ] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter 'FullyQualifiedName~ControllerCli|FullyQualifiedName~ControllerComposition|FullyQualifiedName~MergeCoordinator'`

预期：新 merge 命令和 merge-only composition 测试因接口/接线不存在而失败。

- [ ] **步骤 3：接入 merge-only host**

添加专用于 `merge` 命令的 host/composition 路径，只构造 read-only GitHub state source、`GitAcceptanceVerifier`、`GhMergeGitHubClient` 和 `MergeCoordinator`；不加载设备清单、不创建 Agent、不连接 Desktop session tools。Controller 从 AgentRelay 工具项目启动，`--repository` 必须解析成 Orchestrator/Controller 同一执行主机上的目标项目路径；不支持通过 SSH 在远端运行 Controller。所有 gh working directory、Git fetch/object reads、verifier repository root 均显式指向目标 repo，并验证 canonical GitHub.com origin 等于 Issue URL。对比 CLI expected SHA 与新鲜 GitHub snapshot HEAD 后，才调用 `MergeCoordinator.TryMergeAsync`。扩展 `MergeAttemptResult` 携带已确认的 `MergeCommitSha`，CLI 仅在 GitHub 明确报告 `merged` 且 SHA 是完整对象 ID 时输出 merged；Merge Queue 中仍未合并的 PR 返回 unknown。

- [ ] **步骤 4：运行 CLI 与 merge gate 测试**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter 'FullyQualifiedName~ControllerCli|FullyQualifiedName~ControllerComposition|FullyQualifiedName~MergeCoordinator'`

预期：新命令所有 fail-closed/成功路径测试和既有 merge gate 测试通过；旧 start/status/resume/stop 契约无回归。

### 任务 5：加入可恢复的 Codex Desktop handoff Skill 流程

**文件：**
- 修改：`skills/orchestrator/SKILL.md`
- 创建：`scripts/agentrelay-handoff.ps1`
- 创建：`tests/test_codex_session_orchestration_contract.ps1`
- 创建：`tests/test_codex_session_handoff_state.ps1`
- 参考：`skills/task-entry/SKILL.md`

- [ ] **步骤 1：写 handoff helper 与 Skill 的失败测试**

handoff helper 测试覆盖：origin 与 Issue URL 的 GitHub.com/owner/repository 不同或 host 是 GHES 时，必须在 handoff path 派生及文件读取/创建/替换前失败；验证匹配后才从目标 clone Git common dir 得到状态路径；GitHub.com HTTPS/SSH origin 正确归一化；首次写入与读取保留 phase/thread/SHA 字段；损坏 JSON 不覆盖现有文件；未完成 handoff 不允许换 Issue/project；一个目标 clone 仅一个活动 handoff，测试第二个 Issue 被拒绝；原子替换保留旧状态。Skill 契约测试断言：新任务先选择并验证 Orchestrator repo 后读写目标 repo 状态；恢复使用已保存 Issue/repo/project 并重新验证，不静默换项目；Worker terminal + implementation expected + missing PR 进入 `worker_pr_missing` 且绝不重复 create；no-code-change 路由 Orchestrator/human，failure 暂停，成功报告为结构化 JSON 并要求 PR URL/full SHA/branch/tests/change/Acceptance/workspace evidence；Worker 获准按需 branch/commit/push/create-or-update PR 但禁止 merge；主路径使用 `list_projects`、`create_thread`、`read_thread`；所有角色 repo 身份匹配；Worker 在身份验证后遵循仓库指令，Reviewer 只读必要资料；`create_thread` 前记录 `*_creating`，未知创建结果不重复创建，Worker/Reviewer Thread 分开保存，merge 前先保存 `merging` 和 expected SHA，恢复时重新读取 Thread/GitHub，fetch SHA 与 GitHub 新鲜状态一致。桌面验收确认 Worker/Reviewer 中 Superpowers 可用、Worktree 操作获准；两 Chat 可以从同一基础项目启动，但最终实际 workspace identity 必须与基础/Orchestrator workspace 及彼此不同，且设置不修改基础仓库（包括 `.gitignore`）。不要断言一定新建 Worktree：允许 Superpowers 复用隔离环境。Reviewer 必须在审查前完成 origin/HEAD/BASE 准备，定位准确 PR HEAD、确认 base commit object 可用并输出原始前后 workspace/HEAD/BASE/clean 证据；review range 精确 BASE→HEAD；审查开始后不得改 HEAD/index/branch/workspace files；共享工作区 fallback 必须 BLOCKED。

- [ ] **步骤 2：运行合约测试确认失败**

运行：`pwsh -NoProfile -File tests/test_codex_session_handoff_state.ps1` 和 `pwsh -NoProfile -File tests/test_codex_session_orchestration_contract.ps1`

预期：两个新测试因 helper 和 Desktop 流程不存在而失败。

- [ ] **步骤 3：扩展 Orchestrator Skill 的 Desktop 主路径**

实现 `scripts/agentrelay-handoff.ps1`：`Get-AgentRelayHandoffPath(RepositoryRoot)`、`Get-AgentRelayRepositoryIdentity(RemoteUrl)`、`Test-AgentRelayTargetRepository(RepositoryRoot, IssueUrl)`、`Resolve-AgentRelayWorkspaceIdentity(HostId, Path)`、`Read-AgentRelayHandoff(Path)`、`Write-AgentRelayHandoff(Path, State)`、`Set-AgentRelayHandoffPhase(State, Phase, PauseReason, PausedFromPhase)`。状态 version 固定为 1，包含目标 repo identity、规格中的 phase、`threads.worker/reviewer`（含 host 和 canonical workspace identity）、verification SHA 字段、pause 字段和更新时间；无效状态、损坏文件或覆盖另一个未完成 Issue 时停止。写入前写 sibling 临时文件并 flush，然后同目录原子替换，失败时保留旧状态。Workspace identity 为 `(hostId, canonical physical path)`；Unix 使用 `realpath`，Windows 解析实际目录并对无法排除 symlink/junction alias 的路径 fail closed。不同 host 的相同路径字符串是不同身份；同 host 的同一规范路径视为共享；不比较 Git common dir。

扩展 Orchestrator Skill：GitHub.com Issue identity 明确包含 host/owner/repository，拒绝 GHES。新 handoff 先由用户选择并验证 Orchestrator 项目 origin，再派生该 clone Git common-dir 状态路径；一个 clone 一个活动槽，用户避免跨 clone 并发。恢复按已保存 project/Issue identity 复核，不切换项目。Worker terminal 后按 PR URL/full SHA/branch/tests/change/Acceptance 报告核对；实现预期但缺匹配 PR 时保存 `paused`/`worker_pr_missing`，不重复 create Worker；no-code-change 交 Orchestrator/human，失败暂停。Worker 与 Reviewer 的原始 workspace evidence 字段及 pre/post checks 进入报告并由 Orchestrator 独立核验。Controller 项目和目标 clone 必须都能由 Orchestrator/Controller 执行主机直接访问；若目标 clone 仅在别的 SSH host，暂停，不开发远程 Controller。其他流程保持：origin 验证、base/default branch、fetch SHA、新鲜 GitHub re-read、merge-only CLI 的显式 `--repository`。记录 expected-head 不锁 PR BASE 的残余窗口；base 立即重读，既有 branch protection/Merge Queue 继续生效。


- [ ] **步骤 4：运行 helper 与 Skill 合约测试**

运行：`pwsh -NoProfile -File tests/test_codex_session_handoff_state.ps1` 和 `pwsh -NoProfile -File tests/test_codex_session_orchestration_contract.ps1`

预期：原子状态持久化、主路径、状态恢复、无重复创建、fetch SHA 核对、merge-only CLI 调用和 merge commit SHA 保存以及 legacy 路径边界断言通过。

### 任务 6：落实 Worker/Reviewer 工作区与只读审查契约

**文件：**
- 修改：`skills/reviewer/SKILL.md`
- 修改：`skills/orchestrator/SKILL.md`（若需要同步报告字段）
- 测试：`tests/test_codex_session_orchestration_contract.ps1`

- [ ] **步骤 1：增加 Reviewer 隔离合约测试**

断言 Worker 和 Reviewer 项目 `origin` 匹配 Issue repo；两个角色都调用 Superpowers `using-git-worktrees`，可复用隔离 workspace 或创建 Worktree，但最终 identity 必须分别独立于选定 base/Orchestrator workspace 且 Worker/Reviewer 彼此不同。identity 按 `(hostId, canonical physical path)` 判断：不同 host 同路径不冲突，同 host symlink/junction alias 冲突，Git common dir 相同但实际 worktree 不同不冲突。任何角色共享原工作区 fallback、隔离失败或初始化修改基础 repo（特别是 `.gitignore`）必须 BLOCKED。Worker/Reviewer 报告含 `host_id`、`project_id`、`workspace_path`、`canonical_workspace_path`、`git_top_level`、`git_dir`、`git_common_dir`、`head_sha`、`base_sha`、`working_tree_clean` 及适用的 before/after 值。Reviewer 必须先验证 origin、取得新鲜 PR HEAD/BASE、定位 workspace 到 PR HEAD、验证 BASE commit object 与 clean state；其后 review range 精确为 BASE SHA→HEAD SHA。审查前/后 `git rev-parse HEAD` 等于新鲜 GitHub PR head，`git cat-file -e <base-sha>^{commit}` 成功且 `reviewed_base_sha` 等于新鲜 GitHub PR base；审查开始后 HEAD、index、branch state 和 workspace files 不变。隔离只用 host + canonical physical path 判断，不因 common dir 相同拒绝，也不因不同 git dir 直接认定独立。任一证据无法验证时结果为 `BLOCKED`。同时断言 Reviewer 直接使用 Code Reviewer 标准，不启动 `requesting-code-review` 子代理派发。

- [ ] **步骤 2：运行合约测试确认失败**

运行：`pwsh -NoProfile -File tests/test_codex_session_orchestration_contract.ps1`

预期：现有 Worker/Reviewer Skill 缺少完整隔离、无污染初始化或只读审查边界时，新增断言失败。

- [ ] **步骤 3：更新 Reviewer Skill**

Reviewer Skill 不复制 Superpowers 的 Worktree 创建和代码审查方法；要求调用 `using-git-worktrees`，接受其检测并复用已有隔离环境或按需创建 Worktree 的行为，但禁止共享基础工作区 fallback，且准备过程不得修改基础项目（包括 `.gitignore`）。先验证 origin、获取新鲜 GitHub PR HEAD/BASE、把隔离 workspace 定位到精确 HEAD、验证 BASE commit object 和 clean 状态；这属于 review preparation。随后直接在本 Reviewer Chat 应用 Superpowers Code Reviewer 标准/模板（可参考 `skills/requesting-code-review/code-reviewer.md`），不调用 `requesting-code-review` 的子代理派发，不创建第二层 Reviewer。明确审查范围为 Base SHA→Head SHA。read-only review 开始后不得修改 HEAD、index、branch state 或 workspace files。AgentRelay 报告契约要求 `reviewed_head_sha == reviewed_code_commit == GitHub PR HEAD`、`reviewed_base_sha == GitHub PR BASE`，并报告目标 repo、workspace identity、审查前后 GitHub SHA 与 clean 状态证据；base 对象用 `git cat-file -e <base-sha>^{commit}` 检查可用性，不要求同时 checkout 或从 HEAD 解析 base。任一身份、插件、权限、隔离或 SHA 不确定性返回 `BLOCKED`。

- [ ] **步骤 4：运行 Reviewer Skill 合约测试**

运行：`pwsh -NoProfile -File tests/test_codex_session_orchestration_contract.ps1`

预期：Reviewer 隔离和状态报告断言全部通过。

### 任务 7：集成验证与桌面能力验收

**文件：**
- 修改：`README.md`
- 修改：`docs/device-agent-installation.md`（仅同步 legacy/primary path 说明，如现有文案冲突）
- 修改：`tests/controller/MergeCoordinatorTests.cs`（Merge Queue not-yet-merged）
- 验证：相关 .NET、PowerShell 测试及 Codex Desktop 项目列表

- [ ] **步骤 1：执行桌面端前置能力检查**

用户需先在参与设备 clone 目标仓库并在 Codex Desktop 注册对应基础项目；在 Controller/Orchestrator 执行主机上确认该目标 clone 和 AgentRelay Controller 项目都可直接访问。Worker/Reviewer Chat 均可使用 Superpowers，Worktree skill 可按环境检测复用隔离环境或按需创建。两次 `create_thread` 后检查实际 workspace evidence，两个角色各自均独立于 base/Orchestrator workspace 且彼此不同，不以“新建了 Worktree”作为成功条件；Worktree setup 不得修改基础项目（含 `.gitignore`），Worker/Reviewer 可位于其他 SSH host。验证 Worker branch/commit/push/PR 创建或更新权限及不可 merge 边界。Reviewer 在开始审查前显式定位最新 PR HEAD、验证 BASE 对象存在和 clean 状态，review range 为 BASE→HEAD；审查过程中 HEAD/index/branch/workspace files 不变；`reviewed_base_sha` 精确等于 GitHub PR BASE；前后复读 GitHub SHA 和 clean 状态。验证共享原工作区 fallback 会触发 `BLOCKED`。不要求用户预先创建/注册 Reviewer Worktree，也不要求 Reviewer 内再派发 Code Reviewer 子代理。桌面测试使用安全测试仓库/Issue，不操作真实 PR。

- [ ] **步骤 2：验证 GitHub CLI expected-head 支持**

运行：`gh pr merge --help`

预期：帮助包含 `--match-head-commit`。仅检查本地 CLI 帮助，不对真实 PR 发起合并。

附加自动测试：`MergeCoordinatorTests` 模拟 `gh` 返回成功但 GitHub 复读仍显示 `merged=false`（包括 Merge Queue 已入队）；结果必须保持 unknown/not-complete，且不能记录 `mergeCommitSha`。

- [ ] **步骤 3：运行相关回归测试**

运行：`dotnet test AgentRelay.sln`、`pwsh -NoProfile -File tests/test_codex_session_handoff_state.ps1`、`pwsh -NoProfile -File tests/test_codex_session_orchestration_contract.ps1`、`git diff --check`、`git diff --cached --check`。

预期：所有命令成功；若环境缺少 Desktop session tools、Worker/Reviewer Chat 中的 Superpowers 能力，或无法核验实际 workspace identity、SHA 与干净状态，明确记录为尚未通过的手动验收，不宣称 MVP 完成。

## 执行边界

- 先完成任务 1-4 的测试驱动实现，再调整 Skills；任何失败均 fail closed。
- 任务 6 的 Codex Desktop 检查需要用户可见的 Desktop 会话管理工具，本执行环境若未提供则停在代码/文档已验证、桌面验收待办的状态。
- AgentRelay 不自行创建/管理 Worktree；Worktree 创建委托给 Worker/Reviewer Chat 中的 Superpowers。仍不自动注册 Codex 基础项目，不创建 GitHub Issue，不操作真实 PR。
- 该计划不授权提交、推送、PR 创建或合并。
