# Issue #1 Delivery Boundary Amendment v1

**Status:** Delivery boundary approved by the repository owner on 2026-11-16; amended after conditional review on 2026-11-16 and approved for execution.

**Applies to:** `task_id: codex_session_pr_metadata` in GitHub Issue #1 only.

**Parent plan:** `docs/superpowers/plans/2026-11-14-agentrelay-codex-session-orchestration.md`.

## Approved change

The repository owner's explicit authorization in this chat is: "调整计划：明确授权为该任务隔离改动、提交、推送并创建关联 PR".

For Issue #1 only, this amendment authorizes isolating the Issue-scoped implementation from the existing dirty feature workspace, committing it on `task/codex_session_pr_metadata`, pushing that task branch to the canonical `StudyOrg88/AgentRelay` repository, and creating a linked pull request against `master`.

The 2026-11-14 parent plan remains unchanged. This is a narrow, versioned exception to its no-commit/no-push/no-real-PR execution boundary; it does not authorize changing the default workflow or delivering any other plan task.

## Issue #1 scope precedence

For `task_id: codex_session_pr_metadata` only, this amendment explicitly supersedes the Issue #1 scope restriction stating "不创建真实 Issue/PR".

The authorized delivery scope is:

- Create the task branch `task/codex_session_pr_metadata`.
- Commit only verified Issue #1 changes.
- Push the task branch to the canonical `StudyOrg88/AgentRelay` repository.
- Create one linked pull request targeting `master`.
- Leave the pull request open and unmerged.

All Issue #1 acceptance criteria, required tests, implementation scope, and parent-plan technical requirements remain unchanged. No other Issue or parent-plan task receives this delivery authorization. No other Issue may be created.

## Preserved constraints

- Work from a clean checkout of the canonical repository's latest `master`; verify `origin` matches `https://github.com/StudyOrg88/AgentRelay`.
- Treat the original dirty `feature/agentrelay-cross-device-orchestration` worktree as read-only. Do not reset, clean, stash, switch branches in, overwrite, or reorganize it; inspect it only to identify Issue #1 changes.
- Before creating or using the task branch, check both local and canonical remote refs for `task/codex_session_pr_metadata`. If either exists, inspect its history and content; never overwrite it or force-push.
- Transfer only changes necessary for Issue #1's existing scope and acceptance criteria. Do not copy unrelated tracked, untracked, or generated changes from `feature/agentrelay-cross-device-orchestration`.
- Keep the Issue's acceptance criteria unchanged. Verify them with the commands already specified in Issue #1 and report actual results in the PR.
- Do not merge the PR, change branch protection, delete branches, or represent prior Worker-reported test results as independently rerun evidence.
- If isolating the Issue-specific changes requires implementation or dependencies outside Issue #1's scope, stop and obtain a further Orchestrator/user decision; do not silently expand this amendment.

## Delivery sequence

1. Inspect the isolated source changes read-only and identify the exact files and hunks needed by Issue #1.
2. On a clean checkout of canonical `master`, create `task/codex_session_pr_metadata` only after confirming the local and remote branch do not already exist.
3. Transfer only Issue #1 tests first, run the two Issue-required test commands, and confirm the new tests fail for the intended missing behavior. Then transfer only the corresponding implementation and run the same two commands again.
4. Run the full solution test suite and `git diff --check`; report failures rather than broadening Issue acceptance criteria. Prior Worker-reported results are historical context only, not evidence for this isolated branch.
5. Commit only the verified Issue-scoped result, push without force, and create one linked PR against `master` with `Refs #1` (not an auto-closing keyword). The PR must distinguish tests run in this execution from failed, not-run, and historical test results. Leave it open for independent review and existing merge gates.
