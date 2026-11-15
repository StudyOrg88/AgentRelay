# AgentRelay Personal Orchestration v2 Implementation Plan

> **Status: Superseded for future primary-path work.** The .NET/SSH v2 implementation remains available, but future primary-path changes follow [the Codex session orchestration spec](../specs/2026-11-14-agentrelay-codex-session-orchestration.md) after that spec is approved.

> **面向 AI 代理的工作者：** 计划批准后由当前会话亲自执行；不使用子代理。步骤用复选框跟踪。

**目标：** 将 AgentRelay 收缩为私人使用的单 Issue、单活动任务接力工具。

**架构：** Controller 读取 GitHub 决定角色，按简单轮转选择设备，提交一个任务并持久化其 task ID。固定轮询该任务；完成后读报告、重新读取 GitHub 并接下一棒。

**技术栈：** .NET 10、现有 AgentRelay CLI/SSH、GitHub CLI。

**规格：** `docs/superpowers/specs/2026-11-13-agentrelay-personal-orchestration-v2.md`

## 全局约束

- Windows 与 Ubuntu 可作为本机或 SSH 目标。
- 每个 Issue 同时最多一个活动任务。
- 不自动重试提交确认丢失的任务；保留状态并提示人工核对。
- 保留现有 Reviewer 独立性、Acceptance v1、当前 SHA、required checks 与 PR 状态门槛。
- 不提交、不推送、不操作真实 PR，除非用户另外授权。

## 审查重点（Review Focus）

- Controller 重启后不得创建第二个任务；测试要求按保存的 task ID 查询原任务。
- 临时设备不可达不能丢失 task ID；测试要求后续轮询恢复。
- 终态报告不能替代 GitHub 状态；测试要求报告后重新读取 GitHub。
- 提交确认丢失不能盲目重发；测试要求停在可解释状态。

---

### 任务 1：简化 Controller 的活动任务接力

**文件：**
- 修改：`src/AgentRelay.Controller/OrchestratorService.cs`
- 修改：`src/AgentRelay.Core/OrchestrationJournal.cs`
- 修改：`src/AgentRelay.Core/DeviceScheduler.cs`
- 修改：`tests/controller/OrchestratorServiceTests.cs`
- 修改：`tests/controller/ControllerRecoveryTests.cs`
- 修改：相关 Core scheduler/journal tests

- [x] **步骤 1：调整测试**

覆盖：单任务活动期间只 poll 同一 task ID；controller 重启后继续 poll 同一 task ID；设备暂不可达时保留该 ID 并在下一轮重试；终态后读取报告并刷新 GitHub；submit ack lost 不会再次 submit；设备用简单 round-robin 选择且不维护负载计数。

- [x] **步骤 2：运行目标测试确认失败**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj --filter FullyQualifiedName~OrchestratorService`

- [x] **步骤 3：精简实现**

移除 dispatch intent/hash 重建、接受任务计数、并发调度、分层指数重试及相关 checkpoint 分支。保留一个 active task pointer（Issue、角色、device ID、task ID）；提交结果未知时停止，不自动重新提交。

- [x] **步骤 4：运行 Controller 与 Core 测试**

运行：`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj` 与 `dotnet test tests/core/AgentRelay.Core.Tests.csproj`

### 任务 2：精简 GitHub/merge 接线和操作说明

**文件：**
- 修改：`src/AgentRelay.Controller/Program.cs`（如入口接线所需）
- 修改：`README.md`
- 修改：`docs/superpowers/specs/2026-11-07-agentrelay-cross-device-orchestration-design.md`（标记被 v2 取代）
- 修改：`docs/superpowers/plans/2026-11-07-agentrelay-cross-device-orchestration.md`（标记被 v2 取代）
- 修改：相关 Controller 合约测试

- [x] **步骤 1：新增精简文档合约测试**

断言操作入口只需 start/status/resume/stop；说明是一张 Issue、一个活动 task ID、完成后接力；旧方案标为 superseded，不再作为执行依据。

- [x] **步骤 2：运行合约测试并按现有约定修正文档**

运行现有 PowerShell 合约测试并确认新断言失败。

- [x] **步骤 3：调整说明；CLI 未实现，明确保留为后续工作**

保留现有 PR merge gate 实现；仅简化不必要的通用编排与文档承诺。合并流程需显式配置，不接触真实 PR。

- [x] **步骤 4：验证代码、脚本与差异**

运行：`dotnet test AgentRelay.sln`、`dotnet build AgentRelay.sln --no-restore`、`pwsh -NoProfile -File tests/test_cross_device_contract.ps1`、所有 `tests/test_*.ps1` 与 `git diff --check`。

> `tests/test_cross_device_contract.ps1` 和 Controller CLI 当前不存在；本次运行新建并执行 `tests/test_personal_orchestration_v2_contract.ps1`。Controller CLI 与 Windows/Ubuntu 实机验证仍未交付。
