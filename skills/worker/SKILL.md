---
name: agentrelay-worker
description: Execute or resume one approved AgentRelay GitHub task.
---

# Worker

## 凭据自动选择

本角色默认使用环境变量 `AGENTRELAY_TOKEN_WORKER_1`。Skill 启动时必须先读取该变量；变量存在时将其仅注入当前进程的 `GH_TOKEN`，然后执行 `gh auth status`。变量不存在或认证失败时立即停止，并提示使用用户级持久化变量配置；不得要求用户每次手动粘贴 Token。Token 不得打印、写入文件、Issue、PR 或日志。

配置示例：

```powershell
[Environment]::SetEnvironmentVariable('AGENTRELAY_TOKEN_WORKER_1', '<worker-1-token>', 'User')
```

## 前置检查

读取 `AGENTS.md`、规格和计划，运行 `gh auth status`，确认工作树干净。认证、网络或上下文失败时停止并报告。

## 从 Issue URL 恢复

若由 `agentrelay-task` 交接或用户直接提供 Issue URL，只处理该 Issue，不扫描候选列表、不要求用户再次选择任务。重新读取 Issue 和入口给出的 finding ID；Issue 是范围和验收依据，交接评论只提供导航信息。

检查 `task/<task_id>` 分支及关联 PR。若已存在，必须验证分支名、PR 关联 Issue 和 Issue 正文中的 `task_id` 一致后，在该分支/同一个 PR 上继续；不新建、不覆盖、不关闭原有工作。若发现同名分支/PR 但无法证明归属，停止并报告冲突。

若 PR review 中有结构化 finding：

- `code_defect`：只有当证据表明它违反 Issue 已批准的范围/验收项时才修复。
- `evidence_gap`：只补齐 Issue 已要求且仍可验证的测试或验收证据。
- `contract_ambiguity` 或 `out_of_scope`：停止代码修改，更新 Issue 为 `task:blocked` 并附 finding ID 与原因，交给 Orchestrator 澄清或决定版本化扩展。
- finding 类别缺失、证据与类别矛盾，或不能映射回 Issue 契约：停止并交给 Orchestrator，不猜测分类。

修复时保留原 review 评论，在同一 PR/Issue 中逐个引用 finding ID，记录改动和测试证据。Worker 不得自行出具 review PASS，也不得把自己实现的任务当作独立审查。

## 流程

1. 有 Issue URL 时只加载该 Issue；没有 URL 时，查询 `task:planned` Issue，解析必填字段并过滤 `depends_on` 中未完成的任务。
2. 只有没有指定 Issue URL 且有多个候选时，展示任务信息并等待**人工选择**；提供 URL 即视为用户已选择任务，不自动换派其他任务。
3. 检查目标分支和 PR；匹配现有工作时按上文规则恢复，否则创建 `task/<task_id>` 分支。将 Issue 更新为 `task:in-progress`，记录 Agent/设备和当前阶段。
4. 按任务类型调用 Superpowers：实现用 `test-driven-development`，调试用 `systematic-debugging`，完成前用 `verification-before-completion`，需要请求独立审查时用 `requesting-code-review`。
5. 运行 Issue 要求的测试。规格缺失、冲突或越界时标记 `task:blocked` 并写明原因；无法在范围内修复的测试失败标记 `task:failed`。
6. 新任务用 `gh pr create` 建立关联 PR；恢复任务更新既有 PR。描述变更、逐项 finding ID 响应、测试结果和未解决风险；将 Issue 标记 `task:review` 并回写执行证据。
7. PR 合并交由 Orchestrator 自动验收与合并流程；Worker 不直接合并。只有合并并验收后才将 Issue 标记 `task:done`。
