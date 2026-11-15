# AgentRelay Codex Session Issue Distribution

Date: 2026-11-15

## Authorization and source

The user explicitly requested creation of GitHub Issues after approving the Codex session orchestration specification and implementation plan. This versioned distribution record authorizes Issue creation despite the earlier plan's initial execution boundary stating that it did not authorize Issues. It does not authorize code implementation, real PR operations, commit, push, or merge by this Orchestrator session.

Issue scope and acceptance evidence are derived only from `docs/superpowers/plans/2026-11-14-agentrelay-codex-session-orchestration.md` and its approved specification. The canonical GitHub repository is `StudyOrg88/AgentRelay`; GitHub resolves the local `HiBoy88/AgentRelay` remote to this repository.

## Task graph

| task_id | Plan task | depends_on | Initial label |
| --- | --- | --- | --- |
| `codex_session_pr_metadata` | 1. Issue/PR stage metadata | none | `task:planned` |
| `codex_session_trusted_acceptance` | 2. Acceptance from trusted base | `codex_session_pr_metadata` | `task:blocked` |
| `codex_session_expected_head` | 3. Expected-head merge precondition | `codex_session_pr_metadata` | `task:blocked` |
| `codex_session_merge_cli` | 4. Merge-only CLI | `codex_session_pr_metadata`, `codex_session_trusted_acceptance`, `codex_session_expected_head` | `task:blocked` |
| `codex_session_handoff` | 5. Recoverable Desktop handoff | `codex_session_merge_cli` | `task:blocked` |
| `codex_session_reviewer_contract` | 6. Worker/Reviewer workspace contract | `codex_session_handoff` | `task:blocked` |
| `codex_session_desktop_acceptance` | 7. Integration and Desktop acceptance | tasks 1-6 | `task:blocked` |

All Issues use the repository's Agent task template contract and Acceptance v1 item fields. No issue adds acceptance requirements beyond the approved plan.
