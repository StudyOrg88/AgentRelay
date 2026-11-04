# AgentRelay

AgentRelay 用 GitHub Issue、分支、Pull Request、review 和 Actions 协调不同设备上的编码 Agent。GitHub 是唯一事实来源；无需常驻服务或数据库。

## 默认入口

把目标 GitHub Issue 链接交给任意一个 Codex，并说：

> 使用 `agentrelay-task` 处理这个任务：`<GitHub Issue URL>`

入口会从 GitHub 恢复任务状态，告诉你下一角色、依据和动作；如果当前会话可以安全承担该角色，就继续执行。若需要独立 Reviewer 会话、契约澄清或其他角色接手，会给出具体交接说明，你再决定在哪台设备启动 Codex，并把同一 Issue URL 交给它。

- `worker`：开始或恢复实现；匹配已有分支/PR 时在原工作上继续。
- `reviewer`：在与实现会话独立的新会话中按 Issue 验收契约审查。
- `orchestrator`：澄清任务契约、核对验收与合并门槛，并在所有门槛通过时自动 squash merge。

审查意见中的范围内代码缺陷或证据缺口交给 Worker；任务范围/验收契约含糊或超范围的要求交给 Orchestrator/用户判断，不让 Worker 猜测。自动合并仍要求 Acceptance v1、审查绑定当前代码 SHA、当前 HEAD 的 required CI 成功、PR 状态有效且无阻塞项；任何证据缺失或过期都会停止合并。

复制 `AGENTS.md`、`.github/` 和 `skills/` 到目标仓库即可采用协议。三个 Codex 的设备由用户选择；AgentRelay 不选择设备、不按负载分配、不远程唤醒。网络不可用时本地提交仍可保留；GitHub Issue/PR 协作、Actions 和自动合并会在网络恢复后继续。

Token 配置见 [`docs/credentials.md`](docs/credentials.md)。入口使用只读路由凭据，具体角色 Skill 自动读取各自的持久化环境变量。
