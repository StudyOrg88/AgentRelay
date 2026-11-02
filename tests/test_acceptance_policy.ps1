$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts/acceptance-policy.ps1')
$repo = Join-Path ([IO.Path]::GetTempPath()) ("agentrelay-policy-" + [guid]::NewGuid())
New-Item -ItemType Directory $repo | Out-Null
try {
    git -C $repo init -q
    git -C $repo config user.email test@example.com
    git -C $repo config user.name Test
    New-Item -ItemType Directory (Join-Path $repo 'src'), (Join-Path $repo 'acceptance') | Out-Null
    Set-Content (Join-Path $repo 'src/app.txt') 'v1'
    git -C $repo add .; git -C $repo commit -qm code1
    $code1 = git -C $repo rev-parse HEAD
    Set-Content (Join-Path $repo 'acceptance/task-1.v1.json') '{}'
    git -C $repo add .; git -C $repo commit -qm record
    $record = git -C $repo rev-parse HEAD
    if (-not (Test-AgentRelayRecordParent -Repository $repo -RecordCommit $record -RecordCommitParent $code1)) { throw 'Record parent must match Git graph' }
    if (Test-AgentRelayRecordParent -Repository $repo -RecordCommit $record -RecordCommitParent $record) { throw 'Record must not self-report its own commit as parent' }
    $materialOnly = Test-AgentRelayReviewValidity -Repository $repo -ReviewedCodeCommit $code1 -PullRequestHead $record
    if (-not $materialOnly.valid) { throw 'Acceptance-only change must not invalidate code review' }
    Set-Content (Join-Path $repo 'src/app.txt') 'v2'
    git -C $repo add .; git -C $repo commit -qm code2
    $code2 = git -C $repo rev-parse HEAD
    $codeChange = Test-AgentRelayReviewValidity -Repository $repo -ReviewedCodeCommit $code1 -PullRequestHead $code2
    if ($codeChange.valid -or -not $codeChange.requires_review) { throw 'Code change must require review' }
    $fake = Test-AgentRelayReviewValidity -Repository $repo -ReviewedCodeCommit ('0' * 40) -PullRequestHead $record
    if ($fake.valid) { throw 'Unknown reviewed commit must be rejected' }
    git -C $repo checkout -q --orphan unrelated
    git -C $repo rm -qrf .
    New-Item -ItemType Directory (Join-Path $repo 'acceptance') | Out-Null
    Set-Content (Join-Path $repo 'acceptance/task-1.v1.json') '{}'
    git -C $repo add .; git -C $repo commit -qm unrelated
    $unrelated = git -C $repo rev-parse HEAD
    if ((Test-AgentRelayReviewValidity -Repository $repo -ReviewedCodeCommit $code1 -PullRequestHead $unrelated).valid) { throw 'Non-ancestor review must be rejected' }
} finally { Remove-Item -LiteralPath $repo -Recurse -Force }

$items = @(
    [pscustomobject]@{ owner_task='task-1'; status='passed'; merge_blocking=$true },
    [pscustomobject]@{ owner_task='task-1'; status='failed'; merge_blocking=$false },
    [pscustomobject]@{ owner_task='task-2'; status='failed'; merge_blocking=$true })
if (Test-AgentRelayMergeBlocked -CurrentTask 'task-1' -Items $items) { throw 'Only current blocking items may block' }
$items += [pscustomobject]@{ owner_task='task-1'; status='pending'; merge_blocking=$true }
if (-not (Test-AgentRelayMergeBlocked -CurrentTask 'task-1' -Items $items)) { throw 'Current unpassed blocking item must block' }
Write-Output 'PASS acceptance policy'
