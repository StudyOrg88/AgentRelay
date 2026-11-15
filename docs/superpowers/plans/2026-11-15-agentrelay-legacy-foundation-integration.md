# AgentRelay Legacy Foundation Integration Plan

> **面向 AI 代理的工作者：** 当前会话原生执行；不使用子代理。只集成已提交的基础实现，不触碰原功能分支中的未提交改动。

**目标：** 将旧功能分支已完成的跨设备 .NET/SSH 基础实现作为明确标记的 legacy 能力，独立提交到当前 `master` 并通过 PR 验收。

**架构：** 从 `origin/master` 建立任务分支，按序 cherry-pick 原功能分支中 18 个已提交的基础实现提交。新 Codex Desktop session orchestration 仍是未来主路径；本任务不改变默认入口、不扩展 legacy 能力，也不纳入旧分支未提交内容。

**技术栈：** .NET 10、PowerShell 7、Git/GitHub CLI。

**依据：** 用户已确认把旧功能分支的基础实现作为独立变更纳入 `master`；实现来源为已提交提交范围 `8a83d82..919dc67`，验证命令来自 `docs/superpowers/plans/2026-11-07-agentrelay-cross-device-orchestration.md` 与 `docs/superpowers/plans/2026-11-13-agentrelay-personal-orchestration-v2.md`。

## 全局约束

- 只包含来源分支中 `8a83d82` 到 `919dc67` 的 18 个已提交实现提交；排除 `07cd927` 计划文档提交及所有未提交/未跟踪文件。
- 保留现有 .NET/SSH 路径为 legacy/experimental；不将其设为 Codex Desktop 主路径，不改变已批准的 Codex session orchestration 规格。
- 不新增服务、依赖、自动重试语义或其他功能；不创建真实远程任务，不操作真实 PR，不直接合并。
- 目标分支为 GitHub canonical repo `StudyOrg88/AgentRelay` 的 `master`；通过 `task/agentrelay-legacy-foundation-integration` PR 提交。
- 不覆盖或清理原功能分支工作区内的任何用户文件。

## 审查重点（Review Focus）

- cherry-pick 范围意外带入计划提交、后续 Controller CLI/Codex session 改动或任何工作区未提交文件：任务分支只包含本计划所列实现范围。
- 任一测试项目失败：停止 PR 验收，提供具体失败输出，不以其他测试通过代替。
- PowerShell 合约脚本失败：保留失败并报告，不因旧方案已被取代而删除测试。
- 旧 CLI 被描述为 Codex Desktop 主路径：PR 明确标记 legacy/experimental，当前 Codex Desktop session orchestration 仍为未来主路径。
- GitHub 默认分支和 Issue/PR 身份不一致：仅向 canonical `StudyOrg88/AgentRelay` 的 `master` 提交。

## 文件范围

- 从已提交历史引入 28 个文件：.NET solution/Core/Agent/Controller 基础项目、单元测试、占位设备清单示例及设备安装说明；以来源提交范围的最终 tree diff 为准。
- 创建：本集成计划及该任务的必要 Acceptance v1 记录。
- 不引入或覆盖当前原功能分支工作区中的 README、Skills、Controller CLI、TaskLifecycle、handoff helper、Codex session spec 等未提交改动。

## 实施步骤

1. 创建唯一任务 Issue，记录 `task_id: agentrelay_legacy_foundation_integration`，依赖为空，状态为 `task:planned`。
2. 从最新 `origin/master` 创建 `task/agentrelay-legacy-foundation-integration`，依序 cherry-pick `8a83d82^..919dc67`；检查最终 tree diff 范围。
3. 顺序运行 Core、Controller、Agent 三个测试项目；运行所有 `tests/test_*.ps1`；运行 `git diff --check`。通过后 push 任务分支并创建关联 PR，不直接合并。
4. 为 PR 当前 HEAD 建立 Acceptance v1 记录，获取独立 Reviewer 按该 Issue 验收项审查并绑定精确 SHA；遵循主仓库现行 CI、Review 与 merge gates。

## 验收要求

- 任务分支以最新 `master` 为基底，保留 18 个来源实现提交；最终只含 28 个基础实现文件及本计划/必要验收材料，未提交或未跟踪用户改动不得进入分支。
- `dotnet test tests/core/AgentRelay.Core.Tests.csproj`、`dotnet test tests/controller/AgentRelay.Controller.Tests.csproj`、`dotnet test tests/agent/AgentRelay.Agent.Tests.csproj` 全部通过。
- `tests/test_*.ps1` 全部通过，`git diff --check` 无输出。
- PR 标明这是 legacy/experimental 基础实现；不把旧 CLI 宣称为 Codex Desktop session 主路径。
- PR 的 Acceptance、独立 Review、required CI 和现行 GitHub 门禁全部通过后，才由 Merge Bot 合并。
