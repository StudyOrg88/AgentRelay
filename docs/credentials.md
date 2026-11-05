# AgentRelay 凭据配置

每个角色使用固定的用户级环境变量；调用 Skill 时自动读取，不需要手动设置 `GH_TOKEN`。

```powershell
[Environment]::SetEnvironmentVariable('AGENTRELAY_TOKEN_ROUTER', '<read-only-router-token>', 'User')
[Environment]::SetEnvironmentVariable('AGENTRELAY_TOKEN_WORKER_1', '<worker-1-token>', 'User')
[Environment]::SetEnvironmentVariable('AGENTRELAY_TOKEN_REVIEWER', '<reviewer-token>', 'User')
[Environment]::SetEnvironmentVariable('AGENTRELAY_TOKEN_MERGE_BOT', '<merge-bot-token>', 'User')
```

设置后必须重新打开终端或 Agent 会话。映射如下：

| Skill | 环境变量 |
|---|---|
| `agentrelay-task` | `AGENTRELAY_TOKEN_ROUTER` |
| `agentrelay-worker` | `AGENTRELAY_TOKEN_WORKER_1` |
| `agentrelay-reviewer` | `AGENTRELAY_TOKEN_REVIEWER` |
| `agentrelay-orchestrator` | `AGENTRELAY_TOKEN_MERGE_BOT` |

`agentrelay-task` 使用单独的只读 token 检查 Issue、PR、仓库内容、checks 和 Actions，再决定下一角色；它不写 Issue/PR，也不做合并。建议只授予该仓库的 Contents、Issues、Pull requests、Actions 和 Checks 只读权限。角色流程由相应 Skill 使用自己的既有凭据：Worker 用于任务与 PR 写入，Reviewer 用于审查评论，Orchestrator/Merge Bot 用于自动验收状态写入和满足门槛后的 squash merge。

Skill 会把对应值临时注入当前进程的 `GH_TOKEN`，只用于本次 GitHub CLI 调用。变量缺失或认证失败时停止。Token 不得输出到终端、日志、Issue、PR 或仓库文件。
