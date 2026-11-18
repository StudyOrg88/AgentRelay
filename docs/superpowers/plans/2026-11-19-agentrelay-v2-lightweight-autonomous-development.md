# AgentRelay v2 轻量自主开发实现计划

> **面向 AI 代理的工作者：** 必需子技能：使用 subagent-driven-development（推荐）或 executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 实现一个本地单活 Orchestrator，能在 Human 批准固定计划后自动组织 Worker、独立 Reviewer、CI、最多三轮修复和安全 expected-head 合并。

**架构：** Codex Skills 负责语义判断、Agent 调度和恢复；新的无状态 `AgentRelay.Gate` .NET 10 CLI 负责受保护批准记录、Git ref lease、GitHub checkpoint、Reviewer/CI/Gate 固定规则和最终合并。GitHub 是 Issue、PR、Review、check 与合并事实来源；测试仓库先验证身份、权限、Gate 目标提交与锁竞争，正式仓库只有在独立 Bootstrap 授权后才能启用自动合并。

**技术栈：** .NET 10、xUnit、PowerShell 7、GitHub CLI/REST API、Codex Desktop Skills；不新增云服务、数据库、队列或常驻 Controller。

**规格：** `docs/architecture/agentrelay-v2.md`，批准提交 `fbe2dc51e278e4d9c50ca873bafe4f0caa654715`

## 全局约束

- Human 批准只绑定不可变计划提交；目标、范围、验收、风险、expected checks 或自动合并授权变化必须重新批准。
- Worker、Reviewer、Gate 和 Merge 使用隔离凭据；Worker 无权写控制/锁 ref、发布可信 Review/Gate check、合并或绕过规则。
- MVP 不依赖 Merge Queue；检测到目标分支要求 Merge Queue 时返回不支持并停止自动合并。
- `agentrelay-gate` 前置判断排除 Gate 自身；发布后才核对完整仓库合并条件。
- Gate check 发布到 GitHub 当前实际要求的 PR HEAD 或测试合并提交，并保留与已审查 HEAD 的关联。
- 每次代码提交使旧 Review 与 Gate 结果失效；实质修复最多三轮，临时故障使用独立有限退避重试。
- 每次 agent dispatch、外部写入、Gate 发布和 merge 前都要验证当前 Orchestrator lease。
- Gate 与 Merge 只运行来自受保护默认分支或已批准发布物、且提交 SHA/摘要匹配 control record 的程序和配置；不得从待审查 Worker PR 工作区加载门禁逻辑。
- Worker 进程不获得原始 GitHub 写 token、可用 credential helper 或可绕过 lease 的远程写路径；其 push、PR 和评论由持有当前 lease 的受控发布路径代办。
- 合并必须携带 `--match-head-commit <full-head-sha>`，禁止 `--admin`、无条件重试和自动删除分支。
- 只有 GitHub 最终状态为 `MERGED` 且有完整 merge commit SHA 才算完成。
- PR 修改 CI、Gate、控制 ref、权限或仓库规则属于特殊风险范围，必须重新取得 Human 批准。
- 真实 GitHub App、权限、保护规则、测试仓库写操作和正式仓库 Bootstrap 均需要实施时的单独明确授权；本计划不授予这些权限。
- 自 v2 重建决策起，暂停旧 Issue #2–#7 和 PR #17 的新增实现、自动审查、修复与自动合并；在 Bootstrap 成功前不改写其合同或关闭记录。
- v2 不兼容 Acceptance v1、旧 Controller 调度路径或旧 workspace 证明链；历史代码暂不删除。

## 文件地图

- `src/AgentRelay.Gate/AgentRelay.Gate.csproj`：无状态 v2 CLI 项目，不引用旧 Controller/Agent 项目。
- `src/AgentRelay.Gate/Program.cs`、`GateCli.cs`：命令解析、稳定 JSON 输出与退出码。
- `src/AgentRelay.Gate/ProcessInvoker.cs`：参数数组方式调用 `gh`，不经 shell 拼接，不泄露凭据。
- `src/AgentRelay.Gate/GitHubApi.cs`：GitHub refs、Issues、PR、Reviews、checks、rules 与 merge 状态的窄接口。
- `src/AgentRelay.Gate/Contracts.cs`：approval、lease、checkpoint、Review、check target 和 Gate 结果的 v2 类型。
- `src/AgentRelay.Gate/BootstrapProbe.cs`：测试仓库只读/主动能力验证与脱敏报告。
- `src/AgentRelay.Gate/ControlPlane.cs`：受保护 approval ref、lease ref 和固定 checkpoint 评论。
- `src/AgentRelay.Gate/GateEvaluator.cs`：Review、expected checks、Gate 目标提交与 pre-gate/final-gate 判定。
- `src/AgentRelay.Gate/MergeExecutor.cs`：expected-head merge 与最终状态复读。
- `tests/gate/*Tests.cs`：与上述组件一一对应的纯单元/契约测试，默认不访问网络。
- `scripts/invoke-v2-bootstrap-probe.ps1`：显式授权后才运行的安全测试仓库探测入口。
- `scripts/invoke-v2-e2e.ps1`：显式授权后才执行的测试仓库端到端场景入口。
- `tests/test_v2_*.ps1`：CLI、Skills、权限边界和 E2E 脚本的静态/假服务契约测试。
- `skills/orchestrator/SKILL.md`：v2 计划批准、lease、checkpoint、调度、修复和恢复主流程。
- `skills/worker/SKILL.md`、`skills/reviewer/SKILL.md`、`skills/task-entry/SKILL.md`：v2 角色边界与恢复入口。
- `AGENTS.md`、`.github/ISSUE_TEMPLATE/task.yml`：新 v2 任务合同；旧 Issue 保持原文不变。
- `docs/v2-bootstrap.md`、`docs/v2-operations.md`、`README.md`：一次性 Bootstrap、日常运行和迁移边界。
- `AgentRelay.sln`：只加入新的 Gate 项目和测试项目，不删除旧项目。

## 审查重点（Review Focus）

- 批准引导循环：普通任务首次写 approval record 和首次获取 lease 都不能要求预持 lease，但任何其他写入不得利用该例外。
- Gate 目标提交竞态：GitHub 在 HEAD 与测试合并提交之间切换或重建测试合并提交时，旧 Gate check 必须失效且不能合并。
- Lease 失权竞态：续约失败、ref 被其他实例推进或 lease 时间不确定时，旧 Orchestrator 及其 Worker 必须因受控写入路径拒绝而无法继续外部写入。
- 可信来源混淆：Worker 发布同名 Review/status 或修改 Gate 程序/配置时，固定 Gate 必须因 App、control ref、lease 或运行时摘要不匹配而拒绝。
- 恢复幂等性：Chat 丢失、进程中断或新设备接管时，不得重复创建分支、PR、Reviewer、Gate check 或 merge attempt。

---

### 任务 1：测试仓库 Bootstrap 能力探测

**task_id:** `v2_bootstrap_probe`  
**depends_on:** `none`

**文件：**
- 创建：`src/AgentRelay.Gate/AgentRelay.Gate.csproj`
- 创建：`src/AgentRelay.Gate/Program.cs`
- 创建：`src/AgentRelay.Gate/GateCli.cs`
- 创建：`src/AgentRelay.Gate/ProcessInvoker.cs`
- 创建：`src/AgentRelay.Gate/GitHubApi.cs`
- 创建：`src/AgentRelay.Gate/Contracts.cs`
- 创建：`src/AgentRelay.Gate/BootstrapProbe.cs`
- 创建：`tests/gate/AgentRelay.Gate.Tests.csproj`
- 创建：`tests/gate/BootstrapProbeTests.cs`
- 创建：`tests/gate/GateCliTests.cs`
- 创建：`scripts/invoke-v2-bootstrap-probe.ps1`
- 创建：`tests/test_v2_bootstrap_contract.ps1`
- 修改：`AgentRelay.sln`

- [ ] **步骤 1：为 read-only probe 编写失败测试**

测试 `BootstrapProbe.EvaluateAsync(BootstrapProbeRequest, IGitHubApi, CancellationToken)`：要求规范 GitHub.com repo、明确 base、非空 expected checks、Reviewer/Gate/Merge 身份各不相同、Worker 无 merge/bypass/control-ref 权限、Gate check 配置预期 App 来源、Merge Queue 在 MVP 中未启用。任一证据缺失返回结构化 `BLOCKED`，不得把 403 或空集合解释为通过。

- [ ] **步骤 2：为 active probe 安全边界编写失败测试**

测试 `probe --active` 必须同时收到 `--test-pr`、`--expected-head` 和 `--authorization-file`；授权记录必须绑定测试仓库、PR、HEAD 和过期时间。active probe 只允许在测试 PR 发布 Reviewer Review 和 Gate check，不修改仓库规则、不合并、不接触正式仓库。

- [ ] **步骤 3：运行测试确认失败**

运行：`dotnet test tests/gate/AgentRelay.Gate.Tests.csproj --filter "FullyQualifiedName~BootstrapProbe|FullyQualifiedName~GateCli"`  
预期：FAIL，因为 Gate CLI 和 probe 尚不存在。

- [ ] **步骤 4：实现最小 CLI、GitHub 接口和 probe**

定义 `GateCli.RunAsync(string[] args, GateDependencies dependencies, TextWriter output, TextWriter error, CancellationToken)`；命令为 `probe --repo <owner/repo> --base <ref> [--active --test-pr <n> --expected-head <sha> --authorization-file <path>]`。输出稳定 JSON，禁止输出 token、原始评论或未过滤的进程输出。`ProcessInvoker` 只接受 executable 与 argument array。

- [ ] **步骤 5：实现 PowerShell 探测入口和静态合同**

脚本只从角色专用环境变量向对应子进程注入 token；默认 read-only。`-Active` 缺明确授权文件时必须停止。合同测试断言脚本不含 `--admin`、不修改 ruleset、不调用 merge。

- [ ] **步骤 6：运行本地验证**

运行：

```powershell
dotnet test tests/gate/AgentRelay.Gate.Tests.csproj
pwsh -NoProfile -File tests/test_v2_bootstrap_contract.ps1
git diff --check
```

预期：全部 PASS；不访问真实 GitHub。

- [ ] **步骤 7：在单独授权下验证安全测试仓库**

先由 Human 授权创建/配置测试仓库身份、refs、checks 和 rules，再运行 read-only probe；随后用绑定测试 PR/HEAD 的短期授权运行 active probe。记录 Reviewer App 是否满足 native review、Gate check 实际目标提交、expected-head 支持、Worker 否定权限和 lease ref 创建竞争。此步骤失败则后续任务保持 blocked，不能在正式仓库试错。

- [ ] **步骤 8：Commit**

```bash
git add AgentRelay.sln src/AgentRelay.Gate tests/gate scripts/invoke-v2-bootstrap-probe.ps1 tests/test_v2_bootstrap_contract.ps1
git commit -m "feat: add v2 bootstrap capability probe"
```

### 任务 2：受保护批准、checkpoint 与任务 lease

**task_id:** `v2_control_plane`  
**depends_on:** `v2_bootstrap_probe`

**文件：**
- 创建：`src/AgentRelay.Gate/ControlPlane.cs`
- 创建：`tests/gate/ApprovalStoreTests.cs`
- 创建：`tests/gate/TaskLeaseTests.cs`
- 创建：`tests/gate/CheckpointStoreTests.cs`
- 修改：`src/AgentRelay.Gate/Contracts.cs`
- 修改：`src/AgentRelay.Gate/GitHubApi.cs`
- 修改：`src/AgentRelay.Gate/GateCli.cs`

- [ ] **步骤 1：编写 approval record 失败测试**

定义 `ApprovalRecord` 字段与规格一致。测试 `ApprovalStore.VerifyAsync(taskId, planCommit)` 只接受控制 ref 上唯一、可解析、完整 SHA 绑定的记录；mutable Issue label/comment、未知 Chat 来源、自动合并未授权、计划字段变化均拒绝。`approval record` 仅接受 Orchestrator 提供的可信 thread/message 标识，不自行从文本推断批准。

- [ ] **步骤 2：编写普通任务批准记录创建测试**

定义 `TrustedApprovalEvent`，包含 Human 身份、可信 thread/message、task、完整 plan SHA、批准时间、风险范围和 auto-merge authority。测试 `ApprovalStore.CreateAsync(TrustedApprovalEvent)` 只接受受信任 Chat 运行环境提供且身份、task、plan 完全匹配的事件，通过受限 control-ref 写入接口以 compare-and-swap 方式创建不可变记录；相同事件重放幂等，不同内容冲突拒绝。首次初始化不要求预先持有 task lease，且该引导例外只适用于创建 approval record，不能写 checkpoint、发布 Gate 或合并。

- [ ] **步骤 3：编写 lease 并发与失权失败测试**

定义 `TaskLeaseRecord` 和 `TaskLeaseService.AcquireAsync/RenewAsync/VerifyOwnershipAsync`。覆盖首次创建冲突、同父提交的两个 contender 只有一个 fast-forward 成功、`force:false`、保守时钟余量、续约失败、owner 不匹配、过期/不确定 lease。验证外部动作 guard 在失权后返回 `BLOCKED`。

- [ ] **步骤 4：编写 checkpoint 幂等失败测试**

定义 `TaskCheckpoint`：task、plan、PR、HEAD、next action、repair round、run ID、thread refs、updated time。测试只更新固定标记评论；重复调用复用同一评论；恢复时 GitHub事实覆盖旧 checkpoint；未知/损坏 checkpoint 不被静默覆盖。

- [ ] **步骤 5：运行测试确认失败**

运行：`dotnet test tests/gate/AgentRelay.Gate.Tests.csproj --filter "FullyQualifiedName~ApprovalStore|FullyQualifiedName~TaskLease|FullyQualifiedName~CheckpointStore"`  
预期：FAIL，因为 control plane 尚未实现。

- [ ] **步骤 6：实现 control plane 和 CLI 命令**

实现 `approval create|verify`、`lease acquire|renew|verify`、`checkpoint read|upsert`。`approval create` 只消费可信运行环境传入的结构化 Human 事件，并使用不可变 CAS 写入；它与首次 `lease acquire` 是两个明确的引导操作，均不要求预持 lease。除这两个引导操作外，所有写操作要求当前 lease guard；后续 lease 更新只用非强制 fast-forward。错误输出使用稳定分类：`invalid_approval`、`approval_conflict`、`lease_held`、`lease_lost`、`checkpoint_conflict`、`api_unavailable`。

- [ ] **步骤 7：运行验证**

运行：

```powershell
dotnet test tests/gate/AgentRelay.Gate.Tests.csproj
git diff --check
```

预期：全部 PASS；fake GitHub 证明竞争与失权路径。

- [ ] **步骤 8：Commit**

```bash
git add src/AgentRelay.Gate tests/gate
git commit -m "feat: add v2 approval lease and checkpoint controls"
```

### 任务 3：可信 Reviewer、expected checks 与 Gate check

**task_id:** `v2_merge_gate_verification`  
**depends_on:** `v2_bootstrap_probe, v2_control_plane`

**文件：**
- 创建：`src/AgentRelay.Gate/GateEvaluator.cs`
- 创建：`src/AgentRelay.Gate/TrustedRuntime.cs`
- 创建：`tests/gate/ReviewVerifierTests.cs`
- 创建：`tests/gate/CheckTargetResolverTests.cs`
- 创建：`tests/gate/GateEvaluatorTests.cs`
- 创建：`tests/gate/TrustedRuntimeTests.cs`
- 修改：`src/AgentRelay.Gate/Contracts.cs`
- 修改：`src/AgentRelay.Gate/GitHubApi.cs`
- 修改：`src/AgentRelay.Gate/GateCli.cs`

- [ ] **步骤 1：编写可信 Review 失败测试**

定义 `ReviewEvidence` 与 `ReviewVerifier.Verify(snapshot, checkpoint, configuredReviewerApp)`。覆盖错误 App、错误 PR/task/session、短 SHA、旧 HEAD、后续 blocking review、Worker 同名评论、空 PASS 证据。只有 Orchestrator checkpoint 中的 reviewer session 与 App 正式 Review 同时匹配当前 HEAD 才通过。

- [ ] **步骤 2：编写 check target 失败测试**

定义 `CheckTargetResolver.Resolve(PullRequestSnapshot, IReadOnlyList<CheckRun>)`。覆盖：测试合并提交有状态时选择它；无状态时选择 PR HEAD；merge commit 与 HEAD 关系无法确认时拒绝；base 更新导致测试合并提交变化时旧目标失效；Merge Queue/`merge_group` 状态在 MVP 中返回 unsupported。

- [ ] **步骤 3：编写 pre-gate 自循环回归测试**

定义 `GateEvaluator.EvaluatePreGateAsync(GateRequest)`。expected checks 非空且全部通过；显式排除尚未发布的 `agentrelay-gate`，不能因整体 merge state 仅被 Gate 自身阻塞而失败；但冲突、其他规则、approval、Review、lease、风险阻断任一失败都必须 BLOCKED。

- [ ] **步骤 4：编写发布与过期测试**

测试 Gate App 为 resolver 返回的目标提交发布 check，并在 details/output 中关联 task、PR、reviewed HEAD、plan、lease owner。HEAD、测试合并提交、approval、blocking state 或 lease owner 任一变化使结果过期。Worker/Gate 来源不匹配的同名状态不满足门禁。

- [ ] **步骤 5：编写受信任 Gate 运行时测试**

测试 `TrustedRuntime.Verify(TrustedRuntimeRequest)`：错误默认分支提交、二进制或配置摘要变化、control record 缺少固定版本、运行目录属于待审 Worker 工作区时，均在加载高权限凭据前 `BLOCKED`；只有来自隔离安装目录或受保护默认分支构建、且 SHA/摘要与 control record 完全匹配的 Gate 才可运行。

- [ ] **步骤 6：运行测试确认失败**

运行：`dotnet test tests/gate/AgentRelay.Gate.Tests.csproj --filter "FullyQualifiedName~ReviewVerifier|FullyQualifiedName~CheckTargetResolver|FullyQualifiedName~GateEvaluator|FullyQualifiedName~TrustedRuntime"`
预期：FAIL，因为 Gate evaluator 尚不存在。

- [ ] **步骤 7：实现 evaluator、受信任运行时和 `gate preflight|publish|verify`**

实现窄接口，不解析自由文本判断业务风险；所有语义结果来自 approval、checkpoint、Review 与明确 blocking flags。入口在读取 Gate 凭据前先调用 `TrustedRuntime`，禁止从待审 PR 工作区解析或执行 Gate 程序/配置。`publish` 前后都验证 lease；`verify` 检查 GitHub 当前实际要求的目标提交和关联 HEAD。

- [ ] **步骤 8：运行验证**

运行：

```powershell
dotnet test tests/gate/AgentRelay.Gate.Tests.csproj
git diff --check
```

预期：全部 PASS，包含 Gate 不等待自身的回归测试。

- [ ] **步骤 9：Commit**

```bash
git add src/AgentRelay.Gate tests/gate
git commit -m "feat: verify v2 reviews checks and gate target"
```

### 任务 4：expected-head 合并与结果复读

**task_id:** `v2_safe_merge`  
**depends_on:** `v2_merge_gate_verification`

**文件：**
- 创建：`src/AgentRelay.Gate/MergeExecutor.cs`
- 创建：`tests/gate/MergeExecutorTests.cs`
- 创建：`tests/gate/MergeCommandTests.cs`
- 修改：`src/AgentRelay.Gate/GateCli.cs`
- 修改：`src/AgentRelay.Gate/GitHubApi.cs`

- [ ] **步骤 1：编写 merge 参数失败测试**

测试 `MergeExecutor.MergeAsync(MergeRequest)` 只接受完整 expected HEAD；启动 `gh pr merge` 的参数数组必须包含 repo、PR、`--squash`、`--match-head-commit <SHA>`，且绝不包含 `--admin` 或 `--delete-branch`。缺字段在启动进程前失败。

- [ ] **步骤 2：编写最终复验失败测试**

在 merge 调用前重新验证 trusted runtime、approval、Review、非 Gate checks、当前 Gate target/check、完整仓库规则和 lease；任一变化不得调用 gh。绿色 Gate check 不能替代 final validation，Merge 也不得从 Worker worktree 或待审分支加载程序与配置。

- [ ] **步骤 3：编写结果复读与未知结果测试**

覆盖 gh 成功但 PR 未 merged、API 超时、HEAD mismatch、merge conflict、返回 queued/pending、merged 无完整 SHA。MVP 检测 Merge Queue 要求时返回 `unsupported_merge_queue`；只有 `MERGED` + 完整 merge SHA 返回成功；未知结果先复读，禁止盲重试。

- [ ] **步骤 4：运行测试确认失败**

运行：`dotnet test tests/gate/AgentRelay.Gate.Tests.csproj --filter "FullyQualifiedName~MergeExecutor|FullyQualifiedName~MergeCommand"`  
预期：FAIL，因为 merge executor 尚不存在。

- [ ] **步骤 5：实现 `gate merge`**

入口先以 control record 固定的 SHA/摘要验证受信任 Gate/Merge 运行时，再加载 Merge 凭据。输出 `MERGED|BLOCKED|PENDING|UNKNOWN` 及脱敏证据；不自动改 Issue label，由 Orchestrator 在确认 `MERGED` 后处理生命周期。合并 attempt 使用唯一 ID 并在 checkpoint 中先记录，恢复时先读 GitHub。

- [ ] **步骤 6：运行验证**

运行：

```powershell
dotnet test tests/gate/AgentRelay.Gate.Tests.csproj
dotnet run --project src/AgentRelay.Gate -- --help
git diff --check
```

预期：全部 PASS；help 不访问网络。

- [ ] **步骤 7：Commit**

```bash
git add src/AgentRelay.Gate tests/gate
git commit -m "feat: add expected-head v2 merge execution"
```

### 任务 5：v2 Agent 编排、修复循环与恢复合同

**task_id:** `v2_agent_orchestration`  
**depends_on:** `v2_control_plane, v2_merge_gate_verification, v2_safe_merge`

**文件：**
- 修改：`skills/orchestrator/SKILL.md`
- 修改：`skills/worker/SKILL.md`
- 修改：`skills/reviewer/SKILL.md`
- 修改：`skills/task-entry/SKILL.md`
- 修改：`AGENTS.md`
- 修改：`.github/ISSUE_TEMPLATE/task.yml`
- 创建：`tests/test_v2_orchestration_contract.ps1`
- 创建：`tests/test_v2_role_contract.ps1`
- 创建：`tests/test_v2_recovery_contract.ps1`

- [ ] **步骤 1：编写计划批准与 lease 合同测试**

断言 Orchestrator 在任务图/dispatch 前验证 approved plan commit、自动化和风险范围；只允许可信 Chat 事件通过 `approval create` 初始化普通任务批准；获得 lease 后才能写 checkpoint 或创建 Chat；每次 dispatch/外部写入前重验。首次批准写入和首次 lease 获取不互相依赖，其余写入没有引导例外。Issue 文字、标签和 Agent 输出不能自行授权。

- [ ] **步骤 2：编写角色和可信来源合同测试**

断言 Worker 不持有 GitHub 写、Reviewer、Gate、Merge 或 control token，运行环境禁用可用 credential helper；Worker 只产生本地提交和结构化发布请求，由 Orchestrator 的受控发布路径在重验 lease 后执行 push、建 PR 和评论。Reviewer 独立 session、专用 App、从实际 PR 审查且不改代码；Orchestrator 将 session 写 checkpoint；Merge Gate 只接受匹配 App/session/HEAD 的结果。取消旧 Acceptance v1 与物理 workspace 证明字段对新 v2 任务的要求。

- [ ] **步骤 3：编写修复轮次合同测试**

测试代码/阻断 Review/普通冲突各消耗一轮，最多三轮；多个 finding 可同轮处理；新 HEAD 必须重新 CI/Review。网络、限流、CI 排队和暂时不可读 Chat 使用独立有限退避，不消耗实质轮次。非阻断建议不触发 Worker 循环。

- [ ] **步骤 4：编写恢复幂等合同测试**

测试原 Chat 丢失时从 approval、lease、Issue、PR、Review、checks 和 checkpoint 推导 next action；已有分支/PR/Review/Gate/merge 不重复创建；lease 活跃时第二 Orchestrator 不派发；保守过期接管后重读全部 GitHub事实。接管后旧 Worker 即使继续运行，也因没有原始写凭据且旧 Orchestrator 的发布路径失去 lease 而无法 push、建 PR 或评论；不能用提示词中的“停止”作为此断言的证据。

- [ ] **步骤 5：运行测试确认失败**

运行：

```powershell
pwsh -NoProfile -File tests/test_v2_orchestration_contract.ps1
pwsh -NoProfile -File tests/test_v2_role_contract.ps1
pwsh -NoProfile -File tests/test_v2_recovery_contract.ps1
```

预期：FAIL，因为现有 Skills/AGENTS/Issue 模板仍要求旧 Acceptance v1 与旧流程。

- [ ] **步骤 6：更新 v2 Skills 与仓库任务合同**

新 Issue 记录 task、approved plan SHA、depends_on、目标、范围、验收、expected checks、risk scope、auto-merge authority；不再复制 Acceptance v1 字段。Orchestrator 使用 Gate CLI 做确定性操作，Skills 不重复实现 YAML/CI/ref 解析。Worker 运行于无 GitHub 写凭据的隔离环境，外部写请求交给复用 `ProcessInvoker` 的 Orchestrator 受控发布路径；该路径在每个动作前验证 lease，失权立即拒绝且不缓存可复用 token。旧 Issue 不被自动迁移。

- [ ] **步骤 7：运行合同测试与旧路径隔离检查**

运行所有 `tests/test_v2_*.ps1`，并确认旧测试只作为历史路径测试存在，不被新任务合同误用。运行 `git diff --check`。

- [ ] **步骤 8：Commit**

```bash
git add skills AGENTS.md .github/ISSUE_TEMPLATE/task.yml tests/test_v2_*_contract.ps1
git commit -m "feat: add v2 autonomous agent orchestration contracts"
```

### 任务 6：测试仓库端到端闭环与运维文档

**task_id:** `v2_end_to_end`  
**depends_on:** `v2_agent_orchestration, v2_safe_merge`

**文件：**
- 创建：`scripts/invoke-v2-e2e.ps1`
- 创建：`tests/test_v2_e2e_contract.ps1`
- 创建：`docs/v2-bootstrap.md`
- 创建：`docs/v2-operations.md`
- 修改：`README.md`

- [ ] **步骤 1：编写 E2E 脚本安全合同测试**

脚本必须要求测试仓库 allowlist、bootstrap authorization、approved plan SHA 和测试 task ID；默认 dry-run；禁止 `--admin`、正式仓库默认值、分支删除和生产凭据。每个外部写入前调用 lease verify；Worker 子进程环境不得包含 GitHub 写 token 或有效 credential helper。

- [ ] **步骤 2：定义真实场景与可复核断言**

覆盖：正常小功能自动合并；普通任务可信批准记录可创建并复读；Worker 无法冒充 Reviewer/Gate/merge；Worker 修改 PR 中的 Gate 源码或配置不能改变实际运行的固定 Gate；新 HEAD 使旧 Review/Gate 失效；CI 失败自动修复；Reviewer 拒绝自动修复；三轮耗尽升级 Human；临时故障不耗轮次；关机/进程中断恢复；第二设备抢锁失败并在保守过期后接管；接管后旧 Worker 的直接 push 和旧发布请求都被拒绝；未经批准范围/高风险阻断；test merge commit Gate target；最终 merge SHA 确认。

- [ ] **步骤 3：运行静态测试确认失败**

运行：`pwsh -NoProfile -File tests/test_v2_e2e_contract.ps1`  
预期：FAIL，因为 E2E 脚本和文档尚不存在。

- [ ] **步骤 4：实现 E2E runner 和操作文档**

runner 只编排既有 CLI/Skills，不复制 Gate 逻辑。文档明确一次性 Bootstrap、凭据环境、权限矩阵、测试仓库配置、故障恢复、三轮升级、正式仓库启用条件和回滚方式。

- [ ] **步骤 5：运行本地全量验证**

运行：

```powershell
dotnet test AgentRelay.sln -m:1
Get-ChildItem tests/test_v2_*.ps1 | ForEach-Object { pwsh -NoProfile -File $_.FullName; if ($LASTEXITCODE) { throw "failed: $($_.Name)" } }
git diff --check
git diff --cached --check
```

预期：全部 PASS；不访问真实 GitHub。

- [ ] **步骤 6：在单独授权下运行安全测试仓库 E2E**

使用任务 1 已验证的配置，逐场景记录 GitHub URL、HEAD、Review App、check target/source、lease ref 前后 SHA、repair round 和最终 merge SHA。任何权限隔离或 Gate 兼容性失败均阻止正式仓库 Bootstrap。

- [ ] **步骤 7：Commit**

```bash
git add scripts/invoke-v2-e2e.ps1 tests/test_v2_e2e_contract.ps1 docs/v2-bootstrap.md docs/v2-operations.md README.md
git commit -m "docs: add v2 bootstrap and end-to-end operations"
```

### 任务 7：正式仓库 Bootstrap 与旧架构迁移

**task_id:** `v2_production_bootstrap`  
**depends_on:** `v2_end_to_end`

**文件：**
- 修改：`README.md`（仅记录已经验证的正式启用状态）
- GitHub 外部状态：控制/锁 refs、Apps、rules、required checks、新 v2 Issues、旧 Issue #2–#7、PR #17

- [ ] **步骤 1：生成不执行的 Bootstrap 变更清单**

列出将创建/修改的 refs、Apps、token 权限、rules、required checks、测试命令、回滚和旧记录迁移；绑定当前默认分支与已批准实施计划 SHA。不得在此步骤写 GitHub。

- [ ] **步骤 2：取得正式仓库 Bootstrap 明确授权**

Human 必须针对清单、目标仓库和风险范围批准。未批准则任务保持 blocked；不得复用规格批准作为配置授权。

- [ ] **步骤 3：按清单初始化并验证正式仓库**

创建 control/lock refs、安装身份、配置 rules/checks；运行与测试仓库相同的权限否定测试和 Gate target 测试。失败则禁用自动合并并回滚本次配置，不绕过保护。

- [ ] **步骤 4：写入受保护 v2 approval record**

将规格与实施计划完整 SHA、Human 批准来源、风险和自动合并授权写入 control ref；复读验证 Worker/Reviewer 无写权限。

- [ ] **步骤 5：迁移 GitHub 历史状态**

确认 v2 机制可用后，给旧 Issue #2–#7 和 PR #17 添加版本化 `superseded-by-v2` 记录并关闭；不合并 PR #17、不删除分支/评论/提交。创建新的 v2 Issues，使用新 task IDs 和依赖图。

- [ ] **步骤 6：验证恢复与自动合并默认关闭/开启状态**

在首次真实任务前复读 rules、expected checks、App source、control ref 和 lease；自动合并只在清单明确授权时开启。

- [ ] **步骤 7：Commit 文档状态**

仅当外部配置和验证真实完成后更新 README；提交内容只陈述可复核结果，不包含 token 或 App 私钥。

## 任务图与并行边界

```text
v2_bootstrap_probe
        ↓
v2_control_plane
        ↓
v2_merge_gate_verification
        ↓
v2_safe_merge
        ↓
v2_agent_orchestration
        ↓
v2_end_to_end
        ↓
v2_production_bootstrap
```

任务 2 的本地 fake 测试可以与任务 1 的人工测试仓库能力配置准备并行，但真实 active probe 证据必须先完成。其余任务顺序依赖前一任务暴露的接口和安全结论，不建议并行修改共享 Contracts/GitHubApi/Skills 文件。

## 完成判定

- 任务 1–6 在安全测试仓库通过规格第 18 节全部验收，且本地全量测试通过。
- 任务 7 只有在独立 Bootstrap 授权后执行；没有该授权时，代码实现可以完成，但正式自动合并保持关闭。
- 任何阶段都不得通过削弱 expected checks、Reviewer/Gate 身份、lease、风险授权或保护规则来让测试通过。
- 旧 Issue/PR 只有在 v2 Bootstrap 真实成功后才标记 superseded；历史始终保留。
