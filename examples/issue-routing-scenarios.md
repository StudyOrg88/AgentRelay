# Issue URL 路由示例

每个场景都从同一个操作开始：把 GitHub Issue URL 交给任意 Codex 并调用 `agentrelay-task`。设备由用户选择；入口只恢复状态并决定角色。

| 场景 | 当前状态 | 下一角色 | 当前会话动作 | 证据 |
|---|---|---|---|---|
| 待执行任务 | `task:planned`，必填字段齐全，依赖已完成，没有 task branch/PR | `worker` | `proceed`：在 `task/<task_id>` 开始实现并更新 Issue | Issue 正文与依赖 Issue 状态；分支/PR 查询无匹配项 |
| 已有分支或 PR | `task:in-progress`，branch 和开放 PR 均可关联到同一个 `task_id` | `worker` | `proceed`：恢复现有 branch/PR，不另开任务 | Issue `task_id`、分支名和 PR 关联一致；保留的提交与评论 |
| 范围内代码修复 | 当前 PR 有 `CHANGES_REQUESTED` finding `R1`，类别 `code_defect`，引用 Issue 验收项 `A2` | `worker` | `proceed`：在同一 PR 修复 `R1`，提交测试证据并逐项回复 | Reviewer finding、验收项 `A2`、当前 PR HEAD 与失败测试证据 |
| 任务契约含糊 | finding `R2` 为 `contract_ambiguity`，Issue 与已批准计划对验收边界表述矛盾 | `orchestrator` | `handoff`：暂停 Worker；Orchestrator 查明冲突并向用户请求必要澄清 | finding `R2`、原 Issue、规格和计划中的冲突位置；保留原契约 |
| 独立审查 | Worker 已打开 PR，当前 HEAD 尚无有效 PASS | `reviewer` | `proceed` 仅当当前 Codex 会话未实现此任务；否则 `handoff` 到新 Codex 会话并复用该 Issue URL | PR 当前完整 HEAD、Issue 验收项、Actions 结果、Reviewer 会话独立性 |
| 自动合并 | 独立 Reviewer 对当前代码 SHA 的 PASS 有效，Acceptance v1 通过，当前 HEAD 的 required CI 成功，PR 开放且可合并 | `orchestrator` | `proceed`：复核所有门槛后执行自动 squash merge；任一项未知则 `blocked` | `reviewed_code_commit` 与 PR HEAD 一致、验收记录/证据有效、GitHub Actions `pull_request` run 的 `head_sha` 完全匹配且 success |
| 已完成任务 | PR 已合并，Issue 为 `task:done`，验收记录完整 | `none` | `complete`：报告已完成，不重复执行或重开任务 | Issue 状态、已合并 PR、合并提交和验收记录 |

若 Issue、PR、评论、review 或 Actions 状态互相矛盾，入口不能挑选较宽松的证据继续；报告冲突并停止。网络/API 不可用也不代表状态为空或任务完成。
