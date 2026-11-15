# AgentRelay Controller CLI 实现计划

> **状态：旧执行路径计划已完成。** 本计划实现自建 .NET/SSH Controller CLI，不接入 Codex Desktop 会话管理工具。新主路径以后续 Codex 会话编排规格为准。

> **面向 AI 代理的工作者：** 必需子技能：使用 executing-plans 逐任务实现此计划。步骤使用复选框（`- [ ]`）语法跟踪。

**目标：** 为单 Issue AgentRelay Orchestrator 增加可运行的 .NET 10 CLI。

**架构：** Controller 项目改为可执行程序集。薄 CLI 解析 `start/status/resume/stop`，用 composition root 从仓库根的 `设备清单.txt`、用户本地状态目录、只读 GitHub token 和现有 transport/merge gate 组装依赖；核心命令处理器可注入测试依赖。

**技术栈：** .NET 10、现有 AgentRelay Controller/Core/Agent 类库和 xUnit，不新增 NuGet 依赖。

**规格：** `docs/superpowers/specs/2026-11-13-agentrelay-controller-cli-design.md`

## 全局约束

- 支持 Windows 与 Ubuntu。
- 只支持一个活动 Issue/task；状态目录使用单实例锁。
- `stop` 只停止 Controller，不取消远端任务。
- 提交确认丢失时不重试 submit。
- status 不调用 GitHub 或目标设备；只读本地 journal。
- GitHub 只读凭据使用 `AGENTRELAY_TOKEN_ROUTER`；自动 merge 沿用 `AGENTRELAY_TOKEN_MERGE_BOT` 与现有 gate。
- CLI 输出不打印 token、Issue 原文、原始评论或未过滤的进程输出。
- 测试只注入 fake，不连接真实 GitHub、不执行真实 merge。

## 审查重点（Review Focus）

- stop 与进程退出竞态：stop 必须唤醒正在等待 poll interval 的循环，远端活动任务仍留在 journal。
- status 并发读取：损坏/未知 journal 返回安全诊断且绝不覆盖已有状态。
- resume 在 ack-lost checkpoint 上不得绕过人工检查或重复 submit。
- 缺 token、无效设备清单或无可调度设备时，start 不得进入 submit。
- CLI 参数/token/异常输出不得泄露凭据或原始 GitHub 内容。

---

### 任务 1：实现可测试的 Controller CLI 命令层

**文件：**
- 创建：`src/AgentRelay.Controller/ControllerCli.cs`
- 修改：`src/AgentRelay.Controller/OrchestratorService.cs`
- 修改：`src/AgentRelay.Core/OrchestrationJournal.cs`（仅在 status 摘要需要只读查询 API 时）
- 创建：`tests/controller/ControllerCliTests.cs`
- 修改：`tests/controller/OrchestratorServiceTests.cs`

- [x] **步骤 1：先写命令处理器测试**

覆盖：未知/缺失命令返回 usage 错误；status 空状态与活动状态给出摘要且不调用 GitHub/Agent；resume 保留 task ID；stop 在循环 poll delay 中被触发后退出并保留 task ID；status 遇到坏 journal 不覆盖文件；输出不包含注入的 secret 字符串。

- [x] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~ControllerCliTests`

- [x] **步骤 3：实现可注入 CLI command handler 和协调停止 API**

定义 `ControllerCli.RunAsync(args, dependencies, output, error, cancellationToken)`；命令仅为 `start <issue-url>`、`status`、`resume <issue-url>`、`stop`。Handler 使用依赖注入的 journal、Orchestrator factory、output writer；stop 使用本地停止信号取消循环等待，不调用 Agent cancel。对用户展示稳定、脱敏的命令错误。

- [x] **步骤 4：运行命令层测试**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~ControllerCliTests`

### 任务 2：组装可执行 Controller 并验证操作契约

**文件：**
- 修改：`src/AgentRelay.Controller/AgentRelay.Controller.csproj`
- 创建：`src/AgentRelay.Controller/Program.cs`
- 创建：`src/AgentRelay.Controller/ControllerComposition.cs`
- 修改：`README.md`
- 修改：`tests/test_personal_orchestration_v2_contract.ps1`
- 创建：`tests/test_controller_cli_contract.ps1`
- 修改：`tests/controller/AgentRelay.Controller.Tests.csproj`

- [x] **步骤 1：编写 composition/CLI contract 测试**

覆盖默认状态目录、从 repo root 读取 `设备清单.txt`、必需 router token、非法/缺失 registry 在任何 submit 前失败、Controller 项目输出可运行 dll，以及四个公开命令和 stop 不取消远端任务。

- [x] **步骤 2：运行测试确认失败**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~ControllerCompositionTests` 和 `pwsh -NoProfile -File tests/test_controller_cli_contract.ps1`。

- [x] **步骤 3：实现最小 composition root 和入口**

将 Controller 项目设为 `Exe`；默认 registry 为当前仓库根 `设备清单.txt`，可用 `AGENTRELAY_REGISTRY` 覆盖；默认状态目录为 `LocalApplicationData/AgentRelay/controller`，可用 `AGENTRELAY_STATE_DIR` 覆盖；poll interval 默认 15 秒，可用 `AGENTRELAY_POLL_SECONDS` 覆盖。通过 `GhGitHubClient`、`GitHubStateReader`、`DeviceTransportOrchestrationAgent`、`OrchestrationJournal` 和 `OrchestratorService` 组装；merge gate 保持现有验证器与 `GhMergeGitHubClient`，但不得降低 gates。

- [x] **步骤 4：更新 README 和用户操作说明**

说明发布/运行命令、repo-root registry、必需 token、`start/status/resume/stop` 及退出码；不声称实机验证已完成。

- [x] **步骤 5：运行完整验证**

运行 `dotnet build AgentRelay.sln --no-restore`、逐个运行 Core/Controller/Agent 测试项目、全部 `tests/test_*.ps1`、`git diff --check`。另运行 `dotnet run --project src/AgentRelay.Controller -- --help`（若提供 help）或无参命令验证 usage/exit code，不连接 GitHub。
