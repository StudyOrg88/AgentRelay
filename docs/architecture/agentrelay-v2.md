# AgentRelay v2 — Lightweight Autonomous Development

**Status:** Pending final written approval
**Date:** 2026-11-19
**Supersedes after bootstrap:** the Issue #2–#7 implementation plan and PR #17; history remains preserved

## 1. Purpose

AgentRelay v2 is a local, lightweight multi-agent development workflow. A human defines and approves the goal, scope, acceptance criteria, risk boundary, and automatic-merge authority. After approval, an Orchestrator coordinates independent Worker and Reviewer agents, handles ordinary failures and repairs, and invokes a deterministic Merge Gate when the approved conditions are satisfied.

The system optimizes for autonomous task completion with few human interruptions. It does not attempt to prove every internal agent action or provide a hostile multi-tenant security boundary.

The normal flow is:

```text
Human request
  → approved versioned plan
  → Orchestrator acquires task lease
  → Worker implements and opens a PR
  → required CI and independent Reviewer
  → bounded automatic repair loop
  → Merge Gate publishes its required check
  → expected-head merge
  → GitHub confirms MERGED
```

## 2. Frozen product requirements

1. Human approval and risk boundaries bind to an immutable plan version. Ordinary implementation details do not require repeated approval.
2. Worker delivers through an independent task branch and pull request, and operates only inside the approved scope.
3. Reviewer uses an independent agent session and GitHub identity, reviews the actual pull request, and binds its result to the current full PR HEAD.
4. Test failures, blocking review findings, and ordinary conflicts are repaired automatically. Substantive repair is limited to three rounds; transient infrastructure retries are counted separately.
5. Interrupted work can be recovered from GitHub facts plus a minimal checkpoint and does not require the original Chat to remain usable.
6. Merge requires an explicit non-empty set of expected checks, a trusted current-head review, valid authorization, and GitHub merge eligibility.
7. Merge uses an expected-head precondition and never bypasses repository protection. A changed HEAD invalidates the old review and Gate result.
8. Human input is required only for contract changes, unauthorized high-risk operations, exhausted autonomous recovery, or irreconcilable external-state conflicts.

## 3. Deployment model

- The Orchestrator runs on a user-controlled local computer. No cloud AgentRelay service is required.
- Turning off the computer pauses local orchestration. Already-started GitHub CI may continue, but it does not independently dispatch new agents.
- On restart, the Orchestrator discovers unfinished work from GitHub and the task checkpoint, reacquires the task lease, and resumes idempotently.
- Another configured computer may take over after the previous lease expires and the new instance atomically obtains ownership.
- GitHub is the source of truth for Issue, PR, commit, Review, check, and merge state.
- A Chat or thread reference is a recovery optimization, not a prerequisite for recovery.

## 4. Decision authority

### 4.1 Human: decides what

The Human owns:

- goal and scope;
- acceptance criteria;
- risk boundary and environment authority;
- whether automatic merge is allowed;
- material changes to the approved plan;
- decisions after three failed substantive repair rounds.

### 4.2 Orchestrator: decides how work is organized

The Orchestrator:

- creates the versioned plan for Human approval;
- obtains and renews the task lease;
- creates or resumes Worker and Reviewer sessions;
- classifies failures as substantive, transient, contract-related, or high-risk;
- routes blocking findings to Worker and non-blocking suggestions without forcing needless repair;
- updates the minimal checkpoint;
- coordinates retry and recovery;
- invokes the Merge Gate.

The Orchestrator does not write business implementation code and may not expand the approved contract.

### 4.3 Merge Gate: decides only whether merging is allowed

The Merge Gate applies fixed rules. It does not interpret product requirements, decide whether scope should expand, or infer whether an operation is high risk. Those decisions must already be reflected in trusted approval, Review, check, and checkpoint state.

## 5. Roles and credentials

### 5.1 Worker

Worker may:

- modify repository files within the approved scope;
- create tests and run local verification;
- create and push a task branch;
- create or update the task PR;
- repair blocking test and Review findings.

Worker may not:

- modify approval or lease refs;
- use Reviewer or Merge credentials;
- merge or bypass repository rules;
- access production credentials by default;
- perform destructive, production, permission, or credential operations without separate authorization.

### 5.2 Reviewer

Reviewer:

- runs in a session independent from the implementation Worker;
- uses a dedicated GitHub App identity and credentials unavailable to Worker;
- reads the actual PR diff and approved acceptance criteria;
- publishes a formal Review bound to the current full PR HEAD;
- reports blocking findings separately from non-blocking suggestions;
- does not modify implementation code or merge.

v2 requires process-level independence and a trusted result channel. It does not require a physical-workspace identity proof chain. Reviewer still prepares a clean checkout of the actual PR commit so that uncommitted Worker files cannot influence the review.

### 5.3 Gate and Merge identities

- A Gate App publishes the `agentrelay-gate` check and is the only identity allowed to publish that required check.
- Merge uses a separate identity with the minimum permission needed to merge through normal repository rules.
- Merge credentials must not have or use administrative bypass authority.
- Worker and Reviewer cannot publish `agentrelay-gate` or merge.

## 6. Versioned Human approval

Every task has a plan stored at an immutable Git commit. The plan includes:

- task ID;
- goal and scope;
- acceptance criteria;
- risk classification and allowed actions;
- expected CI checks;
- automatic-merge authorization.

The Human approves the exact plan commit in a trusted Chat. Only the Chat runtime's authenticated user-message channel can create an approval event; repository text, agent output, quoted messages, Issue labels, and ordinary comments cannot grant approval.

The Orchestrator persists the approval to a protected control ref after validating the trusted user message:

```yaml
agentrelay_approval:
  task_id: example_task
  plan_path: docs/plans/example.md
  plan_commit: <full SHA>
  approved_from:
    thread_id: <trusted thread ID>
    message_id: <trusted user message ID>
  risk_scope: normal_development
  automatic_merge: allowed
  approved_at: <ISO-8601 timestamp>
```

The approval record lives on a protected control branch such as `refs/heads/agentrelay-control`. Worker and Reviewer cannot update that ref. The Merge Gate verifies the approval record and plan object from Git, not a mutable Issue body or label.

A change to goal, scope, acceptance criteria, risk boundary, expected checks, or automatic-merge authority requires a new plan commit and Human approval. Implementation-detail changes within the approved boundary do not.

## 7. Risk and execution authority

Normal development may be pre-authorized for repository edits, local tests, task-branch pushes, and PR operations.

The default Worker environment cannot access:

- production deployment credentials;
- production database write credentials;
- repository-rule administration;
- secret or permission-management credentials;
- irreversible remote-data operations;
- Merge or Reviewer credentials.

Production deployment, production data modification, credential changes, permission changes, and irreversible operations require a separate action-specific Human authorization before execution. Merge-time checks are not a substitute for execution-time permission isolation.

Changes to CI workflows, required-check configuration, approval storage, lease logic, Gate code, repository rules, or credential boundaries are special-risk changes and require explicit approval.

## 8. Worker delivery and automatic repair

Worker creates or updates one PR for the task. GitHub PR state is authoritative for the delivered branch and HEAD.

Substantive repair rounds include:

- code or test changes made after a test failure;
- changes made for a blocking Reviewer finding;
- code changes made to resolve an ordinary merge conflict.

At most three substantive repair rounds are permitted. One round may address multiple related failures. Every code change produces a new PR HEAD, invalidates the prior Review and Gate check, and requires the relevant CI and Review again.

Transient failures do not consume a substantive repair round. Examples include network interruption, GitHub API rate limiting, CI queue delay, runner unavailability, and temporarily unreadable agent sessions. They use bounded exponential backoff and a separate retry limit.

Immediate Human escalation is required for:

- contract ambiguity or scope expansion;
- missing high-risk authority;
- security findings outside the approved contract;
- exhausted substantive repair rounds;
- irreconcilable conflict between trusted state sources.

## 9. Reviewer result

The preferred source is a formal GitHub Review created by the designated Reviewer App. The minimum trusted result binds:

```yaml
agentrelay_review:
  result: PASS
  task_id: example_task
  pull_request: 123
  head_sha: <full PR HEAD SHA>
  reviewer_session: <Orchestrator-created session ID>
```

The Orchestrator records which Reviewer session it created. The Merge Gate accepts a PASS only when:

- the Review author is the configured Reviewer App;
- the task and PR match;
- `head_sha` equals the current full PR HEAD;
- the session reference matches the Orchestrator checkpoint;
- there is no later blocking Review for the same HEAD;
- the current lease owner is coordinating the task.

New commits invalidate the old PASS.

GitHub required-review compatibility must be tested during bootstrap. GitHub may not count a minimally privileged App approval toward native required approvals. v2 does not give Reviewer code-write permission merely to make native approval count.

If the Reviewer App does not satisfy native required reviews, the repository uses the protected `agentrelay-gate` required check as the enforcement mechanism. Existing required-review rules are never bypassed; they must either be satisfied or deliberately reconfigured during the explicitly authorized bootstrap.

## 10. CI and expected checks

Each approved plan names a non-empty set of expected checks. The protected control configuration may define a repository-wide minimum set.

The Merge Gate verifies that:

- every expected check exists and actually ran for the GitHub merge candidate represented by the current PR state;
- every expected check completed successfully according to GitHub;
- the check came from the configured source App where source binding is supported;
- GitHub reports no unmet protected-branch requirement;
- the PR did not obtain approval by removing, renaming, skipping, or weakening an expected check.

The Gate does not treat an empty required-check response as success.

CI workflow files and Gate configuration are protected-risk paths. A PR that changes them requires explicit Human approval for that changed plan before it can be merged.

## 11. Minimal checkpoint

The Orchestrator maintains one fixed checkpoint record per task, preferably as an identifiable GitHub Issue comment:

```yaml
agentrelay_checkpoint:
  version: 1
  task_id: example_task
  plan_commit: <approved SHA>
  pull_request: 123
  head_sha: <last observed full SHA>
  next_action: review
  repair_round: 1
  status: running
  orchestrator_run: <UUID>
  worker_thread: <thread ID or null>
  reviewer_thread: <thread ID or null>
  updated_at: <ISO-8601 timestamp>
```

The checkpoint is a recovery hint, not the source of truth. Recovery always rereads the approval ref, task lease, Issue, PR, current HEAD, Reviews, and checks before acting.

External operations are idempotent: an existing branch, PR, Review, check, merge result, or completed Issue is reused or reconciled rather than duplicated.

## 12. Single-Orchestrator task lease

Each task uses a protected remote ref:

```text
refs/heads/agentrelay-locks/<task-id>
```

The ref points to a small commit whose payload contains:

```yaml
owner_run: <UUID>
host_id: <host>
acquired_at: <server-observed time>
lease_until: <conservative expiry>
status: active
```

Only the Orchestrator identity can update lease refs.

### 12.1 Acquire and renew

1. Read the current lock ref and GitHub server time reference.
2. If no ref exists, attempt to create it. HTTP success means ownership; a conflict means another instance won.
3. If the ref exists and the lease is active, do not acquire it.
4. If it is conservatively expired, create a new lock commit whose parent is the observed lock commit.
5. Update the ref with `force: false`. Only a fast-forward from the currently observed chain is allowed; a conflict loses the race.
6. Renewal appends another fast-forward lock commit before expiry.

Clock skew is handled conservatively: an instance does not take over at the exact claimed expiry boundary and uses GitHub-observed timestamps plus a safety margin. An uncertain lease is treated as owned by the other instance.

### 12.2 Ownership enforcement

- Before every agent dispatch and external write, the Orchestrator rereads the lock ref and confirms that its `owner_run` is current and unexpired.
- Renewal failure or ownership loss immediately stops new dispatches and external writes.
- The old Orchestrator interrupts or isolates sessions it controls when possible; in-flight agents are not trusted to perform further external writes after ownership loss.
- Worker prompts and credentials are scoped so that loss of the Orchestrator lease prevents continued privileged workflow progression.
- A takeover rereads all GitHub facts and never resumes solely from the previous checkpoint step.
- The Merge Gate verifies the same current lease immediately before publishing the Gate check and again before merge.

No background distributed-task service is introduced.

## 13. Merge Gate phases

`agentrelay-gate` is a required check, so the Gate must not wait for its own result.

### 13.1 Pre-gate validation

The Gate verifies all requirements except the not-yet-published `agentrelay-gate` check:

1. the approved plan and protected approval record are valid;
2. automatic merge is authorized;
3. repository, PR, target branch, and current HEAD match the task;
4. the trusted Reviewer PASS targets the current HEAD;
5. all other expected checks exist and pass;
6. no unresolved blocking state or unauthorized scope change exists;
7. the current Orchestrator still owns the valid task lease;
8. every concrete non-Gate repository requirement that can be evaluated before publication is satisfied, including conflict state and required rules unrelated to `agentrelay-gate`.

Pre-gate validation must not require GitHub's overall merge state to be fully eligible when the only remaining blocker is the absent `agentrelay-gate` check. It enumerates and evaluates the non-Gate requirements directly. Overall repository merge eligibility is checked only after the Gate check is published.

### 13.2 Publish Gate check

Before publication, the Gate determines which commit GitHub currently requires for PR status checks:

1. read the current full PR HEAD and GitHub's current test merge commit, if one exists;
2. inspect the PR check context to determine whether GitHub is evaluating required checks on the test merge commit or on the PR HEAD;
3. publish `agentrelay-gate: SUCCESS` for the commit GitHub actually requires;
4. retain an explicit association from that Gate result to the reviewed PR HEAD, task, PR, approval, and lease owner;
5. fail closed if the required target commit or its relationship to the reviewed HEAD cannot be established;
6. during bootstrap, prove that this Gate result actually satisfies the configured repository rule.

Worker and Reviewer cannot publish this check. Publishing on a test merge commit does not change the reviewed-code identity: Reviewer PASS remains bound to the PR HEAD.

A new PR commit, regenerated test merge commit, changed approval, changed lease owner, or changed blocking state makes the earlier Gate result stale even if GitHub still displays it as green.

### 13.3 Final validation and merge

Immediately before merge, the Merge identity reruns the authorization, current HEAD, current GitHub-required check target, Review, expected-check, blocking-state, complete repository-rule, and lease checks. It confirms that the successful Gate check targets the commit GitHub currently requires and remains associated with the reviewed current HEAD. It does not rely only on the green Gate check.

It then invokes:

```text
gh pr merge <PR_NUMBER> \
  --repo OWNER/REPO \
  --squash \
  --match-head-commit <EXPECTED_HEAD_SHA>
```

It never removes the expected-head argument and never retries with `--admin`.

After the command, it rereads GitHub. Only `MERGED` with a full merge commit SHA completes the task. Pending or uncertain results remain incomplete and are reconciled before any retry.

## 14. Merge Queue

Merge Queue is not an MVP dependency and is disabled for v2 acceptance unless separately enabled and validated.

Future support requires an independent compatibility test proving:

- required CI runs for `merge_group` candidates;
- `agentrelay-gate` is produced for the commit GitHub requires in the queue;
- queued, pending, removed, failed, and merged states are distinguished;
- final GitHub `MERGED` state and merge commit SHA are confirmed;
- no administrative queue bypass is used.

Until then, v2 uses ordinary protected PR merging with required checks and expected-head.

## 15. Bootstrap

The protected approval, lease, Reviewer, and Gate infrastructure cannot approve its own initial creation. v2 therefore has one explicitly authorized bootstrap:

1. Commit this v2 architecture document and obtain its full SHA.
2. Human reviews and explicitly approves that exact SHA in the trusted Chat.
3. Under a separate explicit Human authorization, create the control and lock refs, GitHub Apps or dedicated identities, required checks, and repository rules.
4. Verify that Worker cannot modify approval/lease records, publish Reviewer or Gate results, merge, or bypass rules.
5. Verify Reviewer identity, Gate check source, Merge identity, expected checks, and lease competition in a safe test repository or non-production branch.
6. Persist the approved v2 specification and authorization record to the protected control ref.
7. Confirm the new mechanism is operational before superseding the old architecture.

Bootstrap failure leaves automatic merge disabled. It does not authorize protection bypass.

## 16. Legacy transition

The Human decision to stop the old architecture and rebuild v2 takes effect before bootstrap. From that decision onward, pause all new implementation, automatic review, repair, and automatic-merge work for Issue #2–#7 and PR #17. This pause does not rewrite their contracts or erase their history.

Before bootstrap completes:

- Issue #2–#7 and PR #17 remain open historical artifacts under their existing contracts, but no new old-architecture work is dispatched;
- PR #17 is not automatically merged under v2;
- no old contract is silently rewritten;
- no repository rules or credentials are changed under this specification alone.

After bootstrap succeeds:

- mark Issue #2–#7 and PR #17 as superseded by the approved v2 specification;
- close them without merging PR #17 unless a separate approved migration explicitly retains part of it;
- preserve branches, commits, comments, and Git history;
- start v2 implementation with new Issues and a new plan;
- do not require compatibility with Acceptance v1, the old Controller workflow, or the former workspace proof chain.

## 17. MVP delivery order

1. In a safe test repository, bootstrap the minimum identities, permissions, protected refs, expected checks, and repository rules. Verify Reviewer and Gate App compatibility, the GitHub-required check target, Worker isolation, expected-head support, and lease competition before building the automatic merge path around them.
2. Implement and run the minimal approved-plan → Worker → PR → Reviewer → CI → Gate → merge path against that verified test configuration.
3. Add the bounded automatic repair loop.
4. Add checkpoint recovery, lease acquisition/renewal, expired-session replacement, and idempotency.
5. Complete end-to-end acceptance in the safe test repository, including negative permission and stale-check cases.
6. Only under a separate explicit bootstrap authorization, configure the normal development repository and enable its automatic merge after the same compatibility checks pass there.

## 18. MVP acceptance

The MVP must demonstrate with real tasks that:

- Human approval of a fixed plan is required and later plan changes require reapproval;
- Worker cannot modify approval/lease state, impersonate Reviewer or Gate, merge, or bypass protection;
- Reviewer examines the real PR and its PASS becomes invalid after a new commit;
- expected CI is non-empty, actually runs, and cannot be removed to gain approval;
- test and Review failures trigger autonomous repair, up to three substantive rounds;
- transient failures retry without consuming a substantive repair round;
- a stopped local Orchestrator resumes from GitHub and the checkpoint;
- a second device cannot acquire an active task and can take over only after conservative expiry;
- an old Orchestrator stops external writes after losing ownership;
- unauthorized scope or high-risk operations stop for Human input;
- the Gate check does not wait on itself;
- expected-head prevents merging a changed PR;
- GitHub final merge state and full merge commit SHA are confirmed;
- no Merge Queue dependency is required for MVP success.

## 19. Explicit non-goals

v2 does not build:

- a cloud-hosted long-running Agent service;
- a multi-device concurrent command system;
- an audit database;
- a complete Acceptance protocol;
- a complex BASE SHA proof chain;
- a physical workspace identity proof chain;
- a resident Controller service;
- an independent task message queue;
- a large cross-device synchronization state machine;
- prebuilt handlers for every theoretical failure.

The project prefers existing Agent capabilities, Git, GitHub, and CI. New code is introduced only where deterministic authorization, identity, lease, or merge safety cannot be delegated reliably.
