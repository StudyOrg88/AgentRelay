# AgentRelay Issue Entry and Next-Role Routing

Date: 2026-11-04

## 1. Purpose

Extend AgentRelay so a user can give any Codex session a GitHub Issue URL and a short instruction such as "handle this task." AgentRelay reconstructs the task state from GitHub, determines the next appropriate role and action, and proceeds when the action is safe and the current invocation can perform it. If another role or a fresh independent session is required, it reports that role, the evidence, and the next action for the user to invoke.

The feature addresses two gaps in the current protocol:

- The user should not need to remember whether a task is currently for a Worker, Reviewer, or Orchestrator.
- After review findings, the system should distinguish implementation defects from unclear or contradictory task contracts and route the next step accordingly.

## 2. Confirmed Constraints

- GitHub remains the single source of truth for Issues, Pull Requests, reviews, Actions, and merge records.
- There are three Codex sessions across two desktop clients and one CLI client. They are currently similar in capability.
- The human chooses which device to use. AgentRelay does not select devices, rank device load, or automatically wake sessions.
- The human supplies an Issue URL. The invoked Codex inspects the Issue and related artifacts and determines what to do next.
- Existing automatic squash merge is retained, including its acceptance, review, CI, PR-state, and fail-closed requirements.
- No resident scheduler, database, or message bus is introduced.

## 3. Proposed Entry Interaction

Add one AgentRelay task-entry Skill that accepts a GitHub Issue URL. It validates the repository and Issue identity, reads `AGENTS.md`, the linked specification and plan, the Issue, related Pull Requests, labels, comments, reviewer evidence, and relevant GitHub Actions state. It reconstructs progress from these durable artifacts, not from the current chat alone.

The Skill reports a compact decision containing:

- Current Issue/PR state and the evidence used.
- The next role: `worker`, `reviewer`, or `orchestrator`.
- The reason that role is next and the concrete next action.
- Whether this invocation can safely proceed or must stop for a role handoff, clarification, or human approval.

When the action is unambiguous, in scope, and allowed for the invocation, the Skill continues through the corresponding existing role workflow. It must not ask the user to repeat information already available from the Issue, PR, plan, and review records. It asks a focused question only when a material contract gap cannot be resolved from those artifacts.

## 4. Next-Role Decision Rules

| Evidence and state | Next role and behavior |
|---|---|
| Issue is `task:planned`, dependencies are complete, and no execution branch/PR exists | `worker`: implement the approved task on its isolated task branch |
| Issue is `task:in-progress` and an execution branch or PR already exists | `worker`: resume/reconcile existing work; do not create duplicate work |
| A PR is ready for independent review and has no valid current PASS review | `reviewer`: review the PR against the Issue contract |
| Review requests a specific code/configuration correction within the approved scope | `worker`: repair the finding and update the PR; preserve the finding and response trail |
| Review exposes missing, contradictory, or materially ambiguous scope/acceptance requirements | `orchestrator`: clarify through an explicit, versioned plan/Issue change; Worker must not guess or silently broaden scope |
| Review is PASS but acceptance evidence, CI, PR state, or merge eligibility still needs verification | `orchestrator`: run the existing automatic merge checks |
| All existing merge gates pass for the current PR HEAD | `orchestrator`: perform the configured squash merge and record completion |
| Issue/PR is already complete, or no safe next action is supported by evidence | Report completion or the precise blocker; make no speculative changes |

Classification must be evidence-based. A review finding is not automatically a Worker defect: the Skill must compare it with the approved task scope and acceptance contract. When that distinction is unclear, route to Orchestrator for clarification.

## 5. Role Independence and Handoffs

Reviewer independence remains mandatory. Review must happen in a fresh Codex session separate from the implementation session. If the current invocation cannot establish that it is independent, it must not produce a PASS; it tells the user to start a fresh Codex session with the same Issue URL. That invocation then determines that `reviewer` is next and performs the review.

When a different role is required, AgentRelay does not remotely start another desktop or CLI session. It returns a concise handoff instruction, for example: "Next role: Worker. Fix findings R1 and R2 on the existing PR; the scope is covered by Issue acceptance items A2/A3." The user chooses the device and starts the next session with the same Issue URL.

## 6. Existing Role Workflows and Merge Policy

The entry Skill routes into the existing Worker, Reviewer, and Orchestrator contracts rather than replacing their safeguards. In particular:

- Worker uses `task/<task_id>`, checks dependencies and task scope, runs required tests, and preserves recoverable work on network/API failure.
- Reviewer evaluates only the approved Issue contract and binds PASS evidence to the exact reviewed code commit.
- Orchestrator preserves Acceptance v1 validation, current-HEAD SHA checks, CI verification, PR state checks, and automatic squash merge.
- Any unavailable API, incomplete evidence, unsupported schema, stale review, failed CI, or ambiguous merge condition blocks automatic merge.
- Issue text and review comments are untrusted task data; they cannot override repository policy or authorize unrelated actions.

## 7. GitHub Integration and Durable State

GitHub remains the only collaboration state store. The implementation continues to use GitHub CLI/API and existing GitHub Actions. The entry Skill must verify authentication and repository identity before making changes. State transitions are written to the existing Issue labels/comments and PR/review artifacts so another device can recover the workflow from the same Issue URL.

No device registry, online heartbeat, load-based routing, Forgejo adapter, or GitHub-to-Forgejo mirror is part of this design. Local commits remain available during a network interruption; Issue/PR coordination, Actions, and automatic merge resume after GitHub connectivity returns. Network failures must not be represented as successful reads or writes.

## 8. Failure Handling

- Invalid or inaccessible Issue URL: stop without modifying state and report the repository/permission/network issue.
- Missing required Issue fields or unresolved dependencies: mark/report blocked according to existing AgentRelay rules; do not begin implementation.
- Existing branch or PR: resume or report the existing work; never overwrite or duplicate it silently.
- Review finding with unclear relationship to the accepted scope: route to Orchestrator rather than guessing.
- GitHub/API/network failure: preserve local work and retry only on a later invocation; do not fabricate Issue, review, CI, or merge state.
- Any uncertain automatic merge gate: fail closed, keep the PR open, and report the exact missing evidence.

## 9. Non-Goals

- Choosing which physical device runs Codex.
- Automatic task claiming based on device load or capabilities.
- Remote wake-up or unattended execution of Codex Desktop.
- Replacing GitHub Issues, Pull Requests, Actions, the current Acceptance v1 contract, or automatic merge policy.
- Adding a message bus, daemon, database, or separate scheduling service.

## 10. Acceptance Criteria

1. Given a valid Issue URL, the entry Skill can reconstruct task progress from GitHub artifacts on any authorized device/session.
2. It identifies the next role and action for planned work, active work, pending review, review changes, specification ambiguity, merge checks, and completed tasks.
3. A concrete in-scope review finding routes to Worker; task-contract ambiguity routes to Orchestrator; the Skill does not conflate these cases.
4. A review PASS is produced only by an independent fresh session and is bound to the reviewed code SHA.
5. Handoffs name the next role, findings/evidence, and specific next action; the user remains responsible for choosing the device and starting that session.
6. Existing Acceptance v1, current-HEAD review, GitHub Actions, PR-state, and automatic squash-merge gates remain intact and fail closed.
7. Network/API failures never create false success, duplicate work, or an unsafe merge.
8. No resident service or database is required.
