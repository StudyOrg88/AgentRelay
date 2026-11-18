# AgentRelay Codex Session Orchestration Delivery Amendment v1

**Status:** Approved
**Approved on:** 2026-11-18
**Amends:** `docs/superpowers/plans/2026-11-14-agentrelay-codex-session-orchestration.md`
**Applies to:** GitHub Issues #2 through #7 in `StudyOrg88/AgentRelay`

## Purpose

The approved plan was originally bounded to inline execution and therefore did not authorize GitHub Issue distribution or delivery actions. This amendment changes only the delivery mode for the existing tasks so that the already-created Issues can be executed through independent Worker and Reviewer chats.

## Delivery mode

`execution_mode: distributed_issues`

- The Orchestrator may dispatch the existing plan tasks through GitHub Issues #2 through #7 and independent Worker/Reviewer chats.
- A Worker may create or reuse the task branch required by its Issue, commit task-scoped changes, push that branch, and create or update the associated pull request.
- A Worker must not merge a pull request.
- A Reviewer must not modify code or workspace files, commit, push, or merge. A Reviewer may publish the structured review result required by the repository contract.
- The Orchestrator may merge only after the existing AgentRelay acceptance gates pass and the user has provided the required explicit approval.

## Unchanged contract

- The technical scope, dependency graph, acceptance criteria, test requirements, and ownership recorded by the original plan and Issues are unchanged.
- This amendment does not authorize new tasks, expanded acceptance requirements, branch deletion, protection-rule changes, or administrative bypasses.
- Each existing Issue adopts this amendment only through an auditable migration comment that binds the Issue task ID to this file at a full commit SHA on the default branch.
- Migration does not change Issue dependency state. Labels are reconciled separately from the dependency graph.

## Historical preservation

The original plan and existing Issue bodies remain unchanged. This versioned amendment is the sole source for the added delivery authorization; Issue migration comments reference it without duplicating role permissions.
