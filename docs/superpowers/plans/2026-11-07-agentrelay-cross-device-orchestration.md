# AgentRelay 跨设备自动编排实现计划

> **状态：已被取代。** 不再按本计划继续增加通用调度、复杂恢复或完整自动合并平台。个人版实现范围请看 [v2 实现计划](2026-11-13-agentrelay-personal-orchestration-v2.md) 和 [v2 规格](../specs/2026-11-13-agentrelay-personal-orchestration-v2.md)。

> **面向 AI 代理的工作者：** 必需子技能：使用 subagent-driven-development（推荐）或 executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法来跟踪进度。

**目标：** 让 AgentRelay Orchestrator 能从 GitHub Issue 自动派发隔离的 Codex CLI Worker/Reviewer 任务到本机或 Windows/Ubuntu SSH 设备，持续监控、恢复并安全推进到 PR 合并。

**架构：** 控制端是持续运行的本地进程，使用 GitHub 恢复任务生命周期并维护原子化本地运行日志。设备代理在登记的工作区提供统一的预检、幂等提交、状态、报告和取消接口；任务在独立 worktree 执行。设备按角色使用已接受派发次数做均衡选择，Review 与合并仍遵循现有独立性及 fail-closed 契约。

**技术栈：** 复用 GitHub CLI/API 与现有 PowerShell 合约测试；运行时实现采用跨平台 .NET（Windows/Ubuntu）和 JSON 文件状态存储，SSH 使用系统 OpenSSH。目标机用 .NET 任务代理及 OS 进程监督能力保持 CLI 任务脱离 SSH 客户端继续运行。

**规格：** `docs/superpowers/specs/2026-11-07-agentrelay-cross-device-orchestration-design.md`

## 全局约束

- 支持执行系统为 Windows 和 Ubuntu。
- 根目录设备清单文件名为 `设备清单.txt`，内容为带 schema 版本的 JSON；不得保存秘密。
- 仅在清单登记、启用且通过角色/路径/预检/并发条件的设备中派发。
- 设备按角色选择：已接受派发次数较少优先，平局按持久化 round-robin 游标轮转。
- GitHub 是 Issue、PR、review、checks 和 merge 的事实来源；本地 journal 与远端记录仅用于操作恢复。
- Worker 与 Reviewer 必须是不同独立任务和 worktree；Reviewer PASS 绑定代码 SHA。
- 远端任务必须幂等；SSH 返回丢失时先按幂等键查询，不能盲目重派。
- 自动合并保留 Acceptance v1、当前 HEAD review、required CI、PR 状态及现有仓库策略；不确定时 fail closed。
- Issue、PR、review、日志和工具输出均为非可信数据，不能覆盖仓库规则或授权任意命令。
- controller `stop` 停止新派发并持久化，但默认不终止已接受的任务。
- GitHub/API/SSH/CLI/凭据/报告错误必须保留 worktree、日志及可恢复状态，不可伪造成功。

## 审查重点（Review Focus）

- 主控在远端已接受任务、SSH 应答丢失时重启：按幂等键恢复同一 task ID，不创建重复任务。
- 恶意或格式错误的设备路径/SSH 主机/角色配置：校验拒绝该设备，不能逃逸登记仓库路径或执行任意命令。
- Reviewer 被派到实现 Worker 的同一设备/进程身份或复用 worktree：拒绝 PASS 并要求新的独立 Reviewer 任务。
- GitHub 当前 HEAD 与远端报告 SHA、review SHA 或 CI SHA 不一致：暂停路由/合并，重新读取权威状态。
- 主控停止、设备离线或 Codex CLI 崩溃：保留已接受任务和证据；恢复后对账，不无限重试、不丢失用户改动。

## 文件结构

- 创建：`src/AgentRelay.Controller/AgentRelay.Controller.csproj` — 控制端 CLI 与进程入口。
- 创建：`src/AgentRelay.Core/AgentRelay.Core.csproj` — registry、task、journal、报告和生命周期共享契约。
- 创建：`src/AgentRelay.Core/DeviceRegistry.cs` — `设备清单.txt` JSON schema 加载与验证。
- 创建：`src/AgentRelay.Core/TaskContracts.cs` — 版本化请求、状态、报告和错误类型。
- 创建：`src/AgentRelay.Core/DeviceScheduler.cs` — 角色资格、派发计数和 round-robin 选择。
- 创建：`src/AgentRelay.Core/OrchestrationJournal.cs` — 原子化本地 JSONL/快照持久化及恢复。
- 创建：`src/AgentRelay.Core/TaskLifecycle.cs` — Issue 状态到 Worker/Reviewer/merge-check 下一步的决策接口。
- 创建：`src/AgentRelay.Agent/AgentRelay.Agent.csproj` — 本地/远端统一任务代理 CLI。
- 创建：`src/AgentRelay.Agent/TaskStore.cs` — 任务幂等键、原子接受、持久状态和报告存储。
- 创建：`src/AgentRelay.Agent/WorktreeManager.cs` — allowlist 内 worktree 创建、恢复与保留。
- 创建：`src/AgentRelay.Agent/ProcessRunner.cs` — 固定角色命令启动、脱离 SSH 生命周期、日志脱敏。
- 创建：`src/AgentRelay.Controller/DeviceTransport.cs` — local 与结构化 OpenSSH 请求适配。
- 创建：`src/AgentRelay.Controller/GitHubStateReader.cs` — 使用 `gh` 读取 Issue/PR/review/check 的当前权威状态。
- 创建：`src/AgentRelay.Controller/OrchestratorService.cs` — 调度、提交、连续轮询、恢复和生命周期接力。
- 创建：`src/AgentRelay.Controller/Program.cs` — `start|status|resume|stop` 命令入口。
- 创建：`设备清单.txt` — 示例 schema 文档，不含任何可用凭据或真实主机秘密。
- 修改：`AGENTS.md`、`README.md` — 新调度器默认用法、安装和安全边界。
- 修改：`skills/task-entry/SKILL.md` — 将人工手动接力规则与持续 controller 入口衔接，保留纯手动 fallback。
- 修改：`skills/worker/SKILL.md` — 让 Worker 接收结构化任务上下文、task ID 和报告输出位置。
- 修改：`skills/reviewer/SKILL.md` — 确认独立 task/session/worktree 身份并输出可校验报告。
- 修改：`skills/orchestrator/SKILL.md` — 将生命周期判定交给 controller，保留人类澄清与既有自动合并门槛。
- 修改：`docs/credentials.md` — controller/目标设备凭据配置与脱敏规则。
- 创建：`tests/core/AgentRelay.Core.Tests.csproj` 及核心单元测试 — registry、调度、幂等、journal、状态决策。
- 创建：`tests/agent/AgentRelay.Agent.Tests.csproj` 及代理测试 — worktree containment、进程生命周期、状态报告。
- 创建：`tests/controller/AgentRelay.Controller.Tests.csproj` 及 controller 测试 — poll/reconcile/dispatch/restart/merge gate。
- 创建：`tests/test_cross_device_contract.ps1` — 既有 PowerShell 文档与合约回归入口。
- 创建：`examples/device-registry.txt` — Windows/Ubuntu 样例配置，所有 host/path 使用明显占位值。

## 实现任务

### 任务 1：建立跨平台 .NET 解决方案与版本化任务契约

**文件：**
- 创建：`src/AgentRelay.Core/AgentRelay.Core.csproj`
- 创建：`src/AgentRelay.Core/TaskContracts.cs`
- 创建：`src/AgentRelay.Controller/AgentRelay.Controller.csproj`
- 创建：`src/AgentRelay.Agent/AgentRelay.Agent.csproj`
- 创建：`tests/core/AgentRelay.Core.Tests.csproj`
- 创建：`tests/core/TaskContractsTests.cs`

- [ ] **步骤 1：编写失败的契约测试**

覆盖 `TaskRequest` 必须有 schema version、task ID、idempotency key、canonical Issue URL、role、device ID、approved repo/workspace、branch 和 lifecycle generation；覆盖状态枚举 `queued|starting|running|succeeded|failed|cancelled|needs_input`；报告缺少身份或含不支持 schema 时解析失败。

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/core/AgentRelay.Core.Tests.csproj`
预期：FAIL，契约类型尚不存在。

- [ ] **步骤 3：创建 .NET 解决方案和最小契约类型**

定义不可变记录 `TaskRequest`、`TaskStatusRecord`、`TaskReport` 和 `AgentRelayRole`，使用 `System.Text.Json` 明确 JSON 命名与 schema version 常量 `agentrelay-task/v1`。保持 Core 无平台专属 API 和外部 NuGet 依赖。

- [ ] **步骤 4：运行契约测试**

运行：`dotnet test tests/core/AgentRelay.Core.Tests.csproj`
预期：PASS，合法 v1 JSON 往返一致，缺字段和未知版本被拒绝。

- [ ] **步骤 5：Commit**

```bash
git add src tests/core
git commit -m "feat: define AgentRelay remote task contracts"
```

### 任务 2：实现设备清单解析与安全校验

**文件：**
- 创建：`src/AgentRelay.Core/DeviceRegistry.cs`
- 创建：`tests/core/DeviceRegistryTests.cs`
- 创建：`examples/device-registry.txt`
- 创建：`设备清单.txt`

- [ ] **步骤 1：编写 registry 失败测试**

测试 Windows/Ubuntu local/SSH 正常记录、重复 device ID、未知 OS/role、缺失路径、SSH 主机缺字段、秘密字段、相对路径逃逸与错误 schema。断言无效 entry 不进入可调度集合并返回不含秘密的诊断。

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/core/AgentRelay.Core.Tests.csproj --filter FullyQualifiedName~DeviceRegistry`
预期：FAIL，registry loader 尚不存在。

- [ ] **步骤 3：实现 `DeviceRegistry.Load(path, repositoryRoot)`**

读取 UTF-8 JSON；保留用户要求的 `.txt` 后缀；用 `Path.GetFullPath` 验证本地工作区 containment；schema 定义的属性为 `schema_version`、`devices`、`id`、`name`、`os`、`connection`、`host`、`port`、`user`、`repositories`、`roles`、`enabled`、`max_concurrency`。未知/秘密属性拒绝或报告，不把值写入日志。样例仅含占位符。

- [ ] **步骤 4：运行 registry 测试**

运行：`dotnet test tests/core/AgentRelay.Core.Tests.csproj --filter FullyQualifiedName~DeviceRegistry`
预期：PASS；非法 entry 无法被 scheduler 选中，报告不泄露输入秘密。

- [ ] **步骤 5：Commit**

```bash
git add src/AgentRelay.Core/DeviceRegistry.cs tests/core examples/device-registry.txt 设备清单.txt
git commit -m "feat: validate AgentRelay device registry"
```

### 任务 3：实现角色设备均衡调度

**文件：**
- 创建：`src/AgentRelay.Core/DeviceScheduler.cs`
- 创建：`tests/core/DeviceSchedulerTests.cs`

- [ ] **步骤 1：写调度选择测试**

断言 scheduler 只考虑 enabled、支持角色、项目路径匹配、preflight 成功且未超 concurrency 的设备；选本角色 accepted dispatch 最少者；并列时从持久化游标之后轮转；被拒绝的提交不增加计数。

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/core/AgentRelay.Core.Tests.csproj --filter FullyQualifiedName~DeviceScheduler`
预期：FAIL，scheduler 尚不存在。

- [ ] **步骤 3：实现 `DeviceScheduler.Select(role, candidates, counters, cursor)`**

返回候选 `DeviceEntry` 和新 cursor；计数按 `(device_id, role)` 分开；只由 controller 在 agent accepted 确认后调用 `RecordAcceptedDispatch` 更新计数。无候选时返回具诊断的 `NoEligibleDevice`，不得降级角色。

- [ ] **步骤 4：运行调度测试**

运行：`dotnet test tests/core/AgentRelay.Core.Tests.csproj --filter FullyQualifiedName~DeviceScheduler`
预期：PASS；连续选择在等权候选上确定性轮转。

- [ ] **步骤 5：Commit**

```bash
git add src/AgentRelay.Core/DeviceScheduler.cs tests/core/DeviceSchedulerTests.cs
git commit -m "feat: balance AgentRelay tasks across devices"
```

### 任务 4：实现本地 journal 与原子幂等任务存储

**文件：**
- 创建：`src/AgentRelay.Core/OrchestrationJournal.cs`
- 创建：`src/AgentRelay.Agent/TaskStore.cs`
- 创建：`tests/core/OrchestrationJournalTests.cs`
- 创建：`tests/agent/TaskStoreTests.cs`

- [ ] **步骤 1：编写崩溃恢复和幂等测试**

测试 journal 重启后还原计数、cursor、待提交 intent 和 active task；同一 idempotency key 重复 submit 返回同一个 task ID；两个并发 submit 只有一个接受；不同 request payload 重用同一 key 返回冲突；临时文件写入中断不会破坏上一个有效快照。

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/core/AgentRelay.Core.Tests.csproj --filter FullyQualifiedName~Journal` 与 `dotnet test tests/agent/AgentRelay.Agent.Tests.csproj --filter FullyQualifiedName~TaskStore`
预期：FAIL，journal/task store 尚不存在。

- [ ] **步骤 3：实现 journal 和 task store**

Controller journal 将 versioned JSON snapshot 写入临时文件后原子 rename，并对并发 controller 实例持有单实例 lock。Agent `Submit(request)` 在按 idempotency key 命名的锁区内原子写入接受记录；持久化后才返回 accepted task ID。状态、报告与 log path 以 task ID 分区。

- [ ] **步骤 4：运行存储测试**

运行上述两个 `dotnet test` 命令。
预期：PASS；重复/并发提交不产生第二个 task，损坏新快照时恢复旧快照。

- [ ] **步骤 5：Commit**

```bash
git add src tests/core tests/agent
git commit -m "feat: persist AgentRelay orchestration and task state"
```

### 任务 5：实现 worktree 隔离、进程监督与任务代理 CLI

**文件：**
- 创建：`src/AgentRelay.Agent/WorktreeManager.cs`
- 创建：`src/AgentRelay.Agent/ProcessRunner.cs`
- 创建：`src/AgentRelay.Agent/Program.cs`
- 创建：`tests/agent/WorktreeManagerTests.cs`
- 创建：`tests/agent/ProcessRunnerTests.cs`
- 创建：`tests/agent/AgentCliTests.cs`

- [ ] **步骤 1：编写隔离与 SSH 断开测试**

断言 task worktree 位于 allowlisted repository 下；Worker/Reviewer task ID 生成不同 worktree；外部路径/`..` 被拒绝；agent `preflight|submit|status|report|cancel` CLI 使用结构化 JSON stdin/stdout；agent 接受后 parent/SSH 客户端退出不终止被监督的 Codex CLI 子进程；cancel 只终止匹配 task ID 的进程且保留工作区。

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/agent/AgentRelay.Agent.Tests.csproj`
预期：FAIL，worktree manager、runner 和 CLI 尚不存在。

- [ ] **步骤 3：实现 agent 固定操作接口**

实现 CLI 命令 `preflight`、`submit`、`status`、`report`、`cancel`。请求通过 stdin JSON，响应通过 stdout JSON，stderr 只输出脱敏诊断。只允许 registry 的 repository/workspace 和 `worker|reviewer` 固定 role invocation；不得执行 payload 中任意命令。用 `git worktree add` 建立独立目录。

- [ ] **步骤 4：实现 Windows/Ubuntu 后台存活语义**

启动进程时关闭与 SSH stdin/stdout 的生命周期绑定，将 stdout/stderr 定向到 task 日志；使用 OS 支持的进程组/Job Object 策略确保 Windows OpenSSH session 退出后任务仍存活，Ubuntu 使用独立 session/process group。启动和恢复时从持久 task record 校验实际 PID/process identity。

- [ ] **步骤 5：运行 agent 测试**

运行：`dotnet test tests/agent/AgentRelay.Agent.Tests.csproj`
预期：PASS；集成测试在 Windows 与 Ubuntu CI 各验证一个断开父会话后任务仍可查询的 scenario。

- [ ] **步骤 6：Commit**

```bash
git add src/AgentRelay.Agent tests/agent
git commit -m "feat: add cross-platform AgentRelay task agent"
```

### 任务 6：实现结构化本机/SSH 传输和预检

**文件：**
- 创建：`src/AgentRelay.Controller/DeviceTransport.cs`
- 创建：`tests/controller/DeviceTransportTests.cs`
- 创建：`docs/device-agent-installation.md`

- [ ] **步骤 1：编写传输测试**

测试 local agent 直接调用；Ubuntu/Windows SSH 使用配置中固定 host alias、user、port 和 remote agent executable；payload 以标准输入 JSON 传输；恶意引号/换行不会改变远端命令结构；未知 host key、错误返回码、超时和输出非 JSON 均为失败并脱敏。

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~DeviceTransport`
预期：FAIL，transport 尚不存在。

- [ ] **步骤 3：实现 `DeviceTransport.InvokeAsync(device, operation, payload)`**

local 模式运行已安装 agent executable；SSH 模式只调用固定 agent 子命令，使用 OpenSSH StrictHostKeyChecking，host alias 来自已验证 registry，设置有界 timeout 并使用 stdin 传 JSON。不得将 Issue/body/prompt 拼进 shell 命令行。

- [ ] **步骤 4：编写跨平台部署预检文档并验证**

文档包含 Windows OpenSSH Server/Ubuntu OpenSSH Server、host key 固定、.NET runtime/agent、Codex CLI 登录、Git、approved workspace 的预检命令。执行 transport 测试及 `dotnet test` 相关项目。
预期：PASS；命令构造、注入防护、超时错误映射符合契约。

- [ ] **步骤 5：Commit**

```bash
git add src/AgentRelay.Controller/DeviceTransport.cs tests/controller docs/device-agent-installation.md
git commit -m "feat: connect AgentRelay controller to SSH task agents"
```

### 任务 7：实现 GitHub 当前状态读取和生命周期路由

**文件：**
- 创建：`src/AgentRelay.Controller/GitHubStateReader.cs`
- 创建：`src/AgentRelay.Core/TaskLifecycle.cs`
- 创建：`tests/controller/GitHubStateReaderTests.cs`
- 创建：`tests/core/TaskLifecycleTests.cs`

- [ ] **步骤 1：编写状态读取与角色路由测试**

用可注入 `IGitHubClient` fake 覆盖 planned/dependencies-ready -> Worker，PR-ready/no current PASS -> Reviewer，in-scope findings -> Worker，contract ambiguity/out-of-scope -> needs_input，valid PASS -> Orchestrator gate check，all gates passing -> merge action，merged -> complete。API error 和矛盾标签不得被当成空结果。

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~GitHubStateReader` 与 `dotnet test tests/core/AgentRelay.Core.Tests.csproj --filter FullyQualifiedName~TaskLifecycle`
预期：FAIL，state reader/lifecycle 尚不存在。

- [ ] **步骤 3：实现 GitHub read client adapter**

通过进程安全调用 `gh`，只使用 router 只读 credential 做恢复；解析 canonical Issue/PR identity、依赖、labels/comments/reviews/current HEAD/checks/merge state。任何非零退出、权限不足或响应解析失败返回显式 unavailable/error，不返回空队列。

- [ ] **步骤 4：实现 `TaskLifecycle.Decide(snapshot)`**

返回强类型 `Worker|Reviewer|OrchestratorChecks|NeedsInput|Complete` action 和引用的 Issue/PR evidence IDs；按既有 task-entry 决策顺序处理 unresolved findings、独立 PASS 和 current SHA。不得把 Reviewer/Worker 角色规则合并。

- [ ] **步骤 5：运行路由和读取测试**

运行两个目标 `dotnet test` 命令及现有 task-entry 合约测试。
预期：PASS；所有失败状态 fail closed。

- [ ] **步骤 6：Commit**

```bash
git add src tests/core tests/controller
git commit -m "feat: route AgentRelay lifecycle from GitHub evidence"
```

### 任务 8：实现 controller 轮询、恢复和设备调度循环

**文件：**
- 创建：`src/AgentRelay.Controller/OrchestratorService.cs`
- 修改：`src/AgentRelay.Controller/Program.cs`
- 创建：`tests/controller/OrchestratorServiceTests.cs`
- 创建：`tests/controller/ControllerRecoveryTests.cs`

- [ ] **步骤 1：编写接力/恢复测试**

fake clock/agent/GitHub client 测试：start 持续 poll；accepted task 后 SSH ack lost 可按 key 查询原 task；terminal report 后强制重新读取 GitHub 再派下一角色；controller crash/restart 恢复同 task；stop 持久化并停止新 dispatch 但不 cancel active tasks；poll transient failure bounded backoff；无设备时进入暂停状态。

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~OrchestratorService` 和 `--filter FullyQualifiedName~ControllerRecovery`
预期：FAIL，service/recovery 尚不存在。

- [ ] **步骤 3：实现 `OrchestratorService.RunAsync(issueUrl, cancellationToken)`**

使用配置的 polling interval/backoff；写入 submit intent 后再调度；只在 agent accepted 确认后记录 device counter；poll active tasks 并验证 report identity；terminal 后先 reread GitHub，再由 `TaskLifecycle.Decide` 进入下一角色/暂停/完成。所有状态变更先 journal 原子落盘。

- [ ] **步骤 4：实现 `ResumeAsync()` 对账**

读取 journal 中 active/intended tasks，通过对应设备 `status`/idempotency 查询；再读取 GitHub 比对 branch、PR、SHA、review 和 check。状态冲突或目标不可达时保留 task 并退避，不产生新 key 或重复 branch/PR。

- [ ] **步骤 5：实现 `StopAsync()` 与有界重试**

停止新 dispatch 和 poll 循环前持久化 checkpoint；默认不取消 remote tasks。只有网络查询类瞬态错误按指数 backoff 到配置上限；认证/权限/schema 错误进入 needs_input，不无限重试。

- [ ] **步骤 6：运行 controller 循环测试**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj`
预期：PASS；测试可用 fake clock 确定性验证连续轮询和重启对账。

- [ ] **步骤 7：Commit**

```bash
git add src/AgentRelay.Controller tests/controller
git commit -m "feat: continuously orchestrate AgentRelay role tasks"
```

### 任务 9：实现安全的 Orchestrator merge gate 接入

**文件：**
- 修改：`src/AgentRelay.Controller/GitHubStateReader.cs`
- 创建：`src/AgentRelay.Controller/MergeCoordinator.cs`
- 创建：`tests/controller/MergeCoordinatorTests.cs`
- 修改：`tests/test_acceptance_policy.ps1`（仅当现有覆盖缺少 controller contract 时）

- [ ] **步骤 1：编写 merge fail-closed 测试**

验证 Acceptance schema unsupported/missing、blocking item 未通过、review SHA stale、required check pending/failure、PR closed/conflicted、权限/API 不可用时绝不调用 merge；所有 gate 当前 HEAD 有效且 PASS 后只调用一次 squash merge；ack lost 时先查询 merge state 再决定。

- [ ] **步骤 2：运行测试验证失败**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~MergeCoordinator`
预期：FAIL，MergeCoordinator 尚不存在。

- [ ] **步骤 3：实现 `MergeCoordinator.TryMergeAsync(snapshot)`**

只消费 GitHubStateReader 从当前 PR HEAD 读取并验证的 Acceptance v1/review/CI/PR 证据；继续复用现有 Acceptance policy 语义；未知值全部拒绝。合并通过 `gh pr merge --squash` 并由 controller 重新读取 GitHub 验证完成。

- [ ] **步骤 4：运行 merge 与现有 acceptance 回归测试**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~MergeCoordinator`，并执行 `pwsh -NoProfile -File tests/test_acceptance_policy.ps1`、现有 orchestrator acceptance/automation 合约测试。
预期：PASS；现有 SHA、review、CI 与 PR 条件无放宽。

- [ ] **步骤 5：Commit**

```bash
git add src/AgentRelay.Controller tests/controller tests
git commit -m "feat: preserve AgentRelay merge gates in controller"
```

### 任务 10：连接 Skills、安装文档和端到端双系统验证

**文件：**
- 创建：`src/AgentRelay.Controller/Program.cs` CLI command tests（若任务 1 只建入口壳）
- 修改：`AGENTS.md`
- 修改：`README.md`
- 修改：`skills/task-entry/SKILL.md`
- 修改：`skills/worker/SKILL.md`
- 修改：`skills/reviewer/SKILL.md`
- 修改：`skills/orchestrator/SKILL.md`
- 修改：`docs/credentials.md`
- 创建：`tests/test_cross_device_contract.ps1`
- 创建：`tests/integration/WindowsAgentIntegrationTests.cs`
- 创建：`tests/integration/UbuntuAgentIntegrationTests.cs`

- [ ] **步骤 1：编写文档/Skills contract 测试**

断言 root README 给出 `设备清单.txt` 校验和 controller `start/status/resume/stop`；agent role 输入输出路径一致；README/Skills 不再声称系统绝不选设备、不自动接力；保留人工范围澄清、独立 Reviewer 和全部 merge gate。

- [ ] **步骤 2：运行 contract 测试验证失败**

运行：`pwsh -NoProfile -File tests/test_cross_device_contract.ps1`
预期：FAIL，当前说明仍描述用户手动选设备/手动接力。

- [ ] **步骤 3：更新 Skills 与文档**

记录 controller 持续运行要求、设备清单 schema/秘密边界、注册设备均衡、远端 agent 安装、启动/恢复/停止语义、人工暂停条件。纯手动 issue-entry 保留为 controller 未启用时的显式 fallback。Credentials 文档说明 router/worker/reviewer/merge-bot 最小权限，不改变现有 token 生命周期。

- [ ] **步骤 4：执行完整测试矩阵**

运行 `dotnet test` 全 solution、全部 `pwsh -NoProfile -File tests/test_*.ps1`。Windows CI 验证 Windows agent；Ubuntu CI 验证 Ubuntu agent；两者都验证 CLI/SSH 断开模拟、恢复、取消保留工作区和 secret redaction。
预期：全部 PASS；跨平台协议一致。

- [ ] **步骤 5：Commit**

```bash
git add AGENTS.md README.md skills docs/credentials.md tests
git commit -m "docs: enable cross-device AgentRelay orchestration"
```

## 依赖顺序

任务 1 锁定 wire contract；任务 2、3、4 可在契约之上分别完成；任务 5 依赖任务 1/2/4；任务 6 依赖任务 1/2/5；任务 7 可与 agent 实现并行但依赖任务 1；任务 8 依赖 3、4、6、7；任务 9 依赖 7/8；任务 10 集成所有组件并通过双系统验证。任务 9 不修改 `scripts/acceptance-policy.ps1` 或 Acceptance v1 schema。

## 验收交接

完成所有任务后执行：

```powershell
dotnet test
Get-ChildItem tests -Filter 'test_*.ps1' -Recurse | ForEach-Object { pwsh -NoProfile -File $_.FullName }
```

计划执行前需确认 controller runtime 安装、OpenSSH host key 配置和 Codex CLI 登录方式满足 Windows/Ubuntu 两端预检；任何目标机认证未就绪都应让该设备不参与调度，而不能降低安全门槛。
