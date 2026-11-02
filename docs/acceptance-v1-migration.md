# Acceptance v1 迁移

Acceptance v1 适用于尚未执行的新任务。它把 Issue 中已批准的验收项固化为版本化记录，并将代码审查提交与验收材料提交分开追踪。

旧任务和已批准 MVP 计划不得静默改写。需要迁移时，为该任务新增 `acceptance/<task_id>.v1.json`，保留原 Issue 和计划正文，并在新记录中逐项映射原验收标准。迁移记录本身作为新增提交接受审计。

字段含义：

- `reviewed_code_commit`：Reviewer 实际审查的代码提交。
- `acceptance_base_commit`：开始收集本版验收材料时的代码基线。
- `record_commit_parent`：写入记录的提交之父提交，用于从 Git 图中定位记录；记录文件不得自报其自身提交 SHA。
- `items`：只能来自 Issue 契约；迁移不得借机增加验收要求。

只有验收记录或验收材料变化时，既有代码审查仍有效。有任何其他路径的代码或配置变化时，更新 `reviewed_code_commit` 前必须重新审查。
