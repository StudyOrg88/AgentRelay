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
    $recordPath = Join-Path $repo 'acceptance/task-1.v1.json'
    $schemaPath = Join-Path $root 'docs/acceptance-schema-v1.json'
    $validRecord = [ordered]@{
        schema_version='acceptance/v1'; task_id='task-1'; reviewed_code_commit=$code1
        acceptance_base_commit=$code1; record_commit_parent=$record
        items=@([ordered]@{ id='check'; status='passed'; merge_blocking=$true; evidence_type='command'; accepted_evidence=@('ok'); rejected_evidence=@(); owner_task='task-1' })
    }
    $validRecord | ConvertTo-Json -Depth 10 | Set-Content $recordPath
    git -C $repo add .; git -C $repo commit -qm record-valid
    $record = git -C $repo rev-parse HEAD
    $mainBranch = git -C $repo branch --show-current
    $materialOnly = Test-AgentRelayReviewValidity -Repository $repo -CurrentTask 'task-1' -SchemaPath $schemaPath -ReviewedCodeCommit $code1 -PullRequestHead $record
    if (-not $materialOnly.valid) { throw "Acceptance-only change must not invalidate code review: $($materialOnly.reason)" }
    $validRecord.reviewed_code_commit=('0'*40)
    $validRecord.record_commit_parent=$record
    $validRecord | ConvertTo-Json -Depth 10 | Set-Content $recordPath
    git -C $repo add .; git -C $repo commit -qm mismatched-review-commit
    $mismatchedRecordHead = git -C $repo rev-parse HEAD
    if ((Test-AgentRelayReviewValidity -Repository $repo -CurrentTask 'task-1' -SchemaPath $schemaPath -ReviewedCodeCommit $code1 -PullRequestHead $mismatchedRecordHead).valid) { throw 'Record with a different reviewed code SHA must require review' }
    $validRecord.reviewed_code_commit=$code1
    $validRecord.record_commit_parent=$mismatchedRecordHead
    $validRecord | ConvertTo-Json -Depth 10 | Set-Content $recordPath
    git -C $repo add .; git -C $repo commit -qm restore-reviewed-code-commit
    $record = git -C $repo rev-parse HEAD
    git -C $repo checkout -qb deletion-case $record
    git -C $repo rm -q acceptance/task-1.v1.json
    git -C $repo commit -qm delete-record
    $deleteCommit = git -C $repo rev-parse HEAD
    $validRecord.record_commit_parent=$deleteCommit
    New-Item -ItemType Directory (Join-Path $repo 'acceptance') | Out-Null
    $validRecord | ConvertTo-Json -Depth 10 | Set-Content $recordPath
    git -C $repo add .; git -C $repo commit -qm restore-deleted-record
    $restoredAfterDelete = git -C $repo rev-parse HEAD
    if ((Test-AgentRelayReviewValidity -Repository $repo -CurrentTask 'task-1' -SchemaPath $schemaPath -ReviewedCodeCommit $code1 -PullRequestHead $restoredAfterDelete).valid) { throw 'Deleted then recreated acceptance record must require review' }
    git -C $repo checkout -q $mainBranch
    $otherTaskRecord = [ordered]@{}
    foreach ($key in $validRecord.Keys) { $otherTaskRecord[$key] = $validRecord[$key] }
    $otherTaskRecord.task_id='other-task'
    $otherTaskRecord | ConvertTo-Json -Depth 10 | Set-Content $recordPath
    git -C $repo add .; git -C $repo commit -qm wrong-task-record
    $wrongTaskHead = git -C $repo rev-parse HEAD
    if ((Test-AgentRelayReviewValidity -Repository $repo -CurrentTask 'task-1' -SchemaPath $schemaPath -ReviewedCodeCommit $code1 -PullRequestHead $wrongTaskHead).valid) { throw 'Other-task record must invalidate the accepted evidence path' }
    Move-Item $recordPath (Join-Path $repo 'acceptance/other-task.v1.json')
    git -C $repo add -A; git -C $repo commit -qm renamed-record
    $renamedHead = git -C $repo rev-parse HEAD
    if ((Test-AgentRelayReviewValidity -Repository $repo -CurrentTask 'task-1' -SchemaPath $schemaPath -ReviewedCodeCommit $code1 -PullRequestHead $renamedHead).valid) { throw 'Noncanonical task record path must require review' }
    Set-Content $recordPath ($validRecord | ConvertTo-Json -Depth 10)
    git -C $repo add -A; git -C $repo commit -qm restore-record
    $restoredHead = git -C $repo rev-parse HEAD
    if ((Test-AgentRelayReviewValidity -Repository $repo -CurrentTask 'task-1' -SchemaPath $schemaPath -ReviewedCodeCommit $code1 -PullRequestHead $restoredHead).valid) { throw 'Deleting prior acceptance evidence must require review even when replaced' }
    Set-Content (Join-Path $repo 'src/app.txt') 'v2'
    git -C $repo add .; git -C $repo commit -qm code2
    $code2 = git -C $repo rev-parse HEAD
    $codeChange = Test-AgentRelayReviewValidity -Repository $repo -CurrentTask 'task-1' -SchemaPath $schemaPath -ReviewedCodeCommit $code1 -PullRequestHead $code2
    if ($codeChange.valid -or -not $codeChange.requires_review) { throw 'Code change must require review' }
    $fake = Test-AgentRelayReviewValidity -Repository $repo -CurrentTask 'task-1' -SchemaPath $schemaPath -ReviewedCodeCommit ('0' * 40) -PullRequestHead $restoredHead
    if ($fake.valid) { throw 'Unknown reviewed commit must be rejected' }
    git -C $repo checkout -q --orphan unrelated
    git -C $repo rm -qrf .
    New-Item -ItemType Directory (Join-Path $repo 'acceptance') | Out-Null
    Set-Content (Join-Path $repo 'acceptance/task-1.v1.json') '{}'
    git -C $repo add .; git -C $repo commit -qm unrelated
    $unrelated = git -C $repo rev-parse HEAD
    if ((Test-AgentRelayReviewValidity -Repository $repo -CurrentTask 'task-1' -SchemaPath $schemaPath -ReviewedCodeCommit $code1 -PullRequestHead $unrelated).valid) { throw 'Non-ancestor review must be rejected' }
    $shortShaRejected = $false
    try { Test-AgentRelayRecordParent -Repository $repo -RecordCommit 'abc1234' -RecordCommitParent $code1 | Out-Null } catch { $shortShaRejected = $true }
    if (-not $shortShaRejected) { throw 'Short record SHA must be rejected' }
} finally { Remove-Item -LiteralPath $repo -Recurse -Force }

$items = @(
    [pscustomobject]@{ id='pass'; owner_task='task-1'; status='passed'; merge_blocking=$true; evidence_type='manual'; accepted_evidence=@('ok'); rejected_evidence=@() },
    [pscustomobject]@{ id='optional'; owner_task='task-1'; status='failed'; merge_blocking=$false; evidence_type='manual'; accepted_evidence=@(); rejected_evidence=@() },
    [pscustomobject]@{ id='other'; owner_task='task-2'; status='failed'; merge_blocking=$true; evidence_type='manual'; accepted_evidence=@(); rejected_evidence=@() })
$mergeRoot = Join-Path ([IO.Path]::GetTempPath()) ("agentrelay-merge-" + [guid]::NewGuid())
$mergeDirectory = Join-Path $mergeRoot 'acceptance'
$mergeFixture = Join-Path $mergeDirectory 'task-1.v1.json'
$schemaPath = Join-Path $root 'docs/acceptance-schema-v1.json'
try {
    New-Item -ItemType Directory $mergeDirectory -Force | Out-Null
    git -C $mergeRoot init -q
    git -C $mergeRoot config user.email test@example.com
    git -C $mergeRoot config user.name Test
    Set-Content (Join-Path $mergeRoot 'src.txt') 'reviewed code'
    git -C $mergeRoot add .; git -C $mergeRoot commit -qm reviewed-code
    $reviewedMergeCode = git -C $mergeRoot rev-parse HEAD
    $recordObject = [ordered]@{ schema_version='acceptance/v1'; task_id='task-1'; reviewed_code_commit=$reviewedMergeCode; acceptance_base_commit=$reviewedMergeCode; record_commit_parent=$reviewedMergeCode; items=$items }
    $recordObject | ConvertTo-Json -Depth 10 | Set-Content $mergeFixture
    git -C $mergeRoot add .; git -C $mergeRoot commit -qm valid-record
    $mergeHead = git -C $mergeRoot rev-parse HEAD
    if (Test-AgentRelayMergeBlocked -CurrentTask 'task-1' -Repository $mergeRoot -PullRequestHead $mergeHead -SchemaPath $schemaPath) { throw 'Only current blocking items may block' }
    $items += [pscustomobject]@{ id='pending'; owner_task='task-1'; status='pending'; merge_blocking=$true; evidence_type='manual'; accepted_evidence=@(); rejected_evidence=@() }
    $recordObject.items=$items
    $recordObject.record_commit_parent=$mergeHead
    $recordObject | ConvertTo-Json -Depth 10 | Set-Content $mergeFixture
    git -C $mergeRoot add .; git -C $mergeRoot commit -qm pending-record
    $mergeHead = git -C $mergeRoot rev-parse HEAD
    if (-not (Test-AgentRelayMergeBlocked -CurrentTask 'task-1' -Repository $mergeRoot -PullRequestHead $mergeHead -SchemaPath $schemaPath)) { throw 'Current unpassed blocking item must block' }
    $recordObject.items=@([pscustomobject]@{ id='missing-blocking'; owner_task='task-1'; status='failed'; evidence_type='manual'; accepted_evidence=@(); rejected_evidence=@() })
    $recordObject.record_commit_parent=$mergeHead
    $recordObject | ConvertTo-Json -Depth 10 | Set-Content $mergeFixture
    git -C $mergeRoot add .; git -C $mergeRoot commit -qm malformed-record
    $mergeHead = git -C $mergeRoot rev-parse HEAD
    if (-not (Test-AgentRelayMergeBlocked -CurrentTask 'task-1' -Repository $mergeRoot -PullRequestHead $mergeHead -SchemaPath $schemaPath)) { throw 'Malformed record must fail closed' }
    $recordObject.items=@([pscustomobject]@{ id='missing-status'; owner_task='task-1'; merge_blocking=$true; evidence_type='manual'; accepted_evidence=@(); rejected_evidence=@() })
    $recordObject.record_commit_parent=$mergeHead
    $recordObject | ConvertTo-Json -Depth 10 | Set-Content $mergeFixture
    git -C $mergeRoot add .; git -C $mergeRoot commit -qm missing-status-record
    $mergeHead = git -C $mergeRoot rev-parse HEAD
    if (-not (Test-AgentRelayMergeBlocked -CurrentTask 'task-1' -Repository $mergeRoot -PullRequestHead $mergeHead -SchemaPath $schemaPath)) { throw 'Record missing status must fail closed' }
    $recordObject.task_id='other-task'
    $recordObject.record_commit_parent=$mergeHead
    $recordObject | ConvertTo-Json -Depth 10 | Set-Content $mergeFixture
    git -C $mergeRoot add .; git -C $mergeRoot commit -qm other-task-record
    $mergeHead = git -C $mergeRoot rev-parse HEAD
    if (-not (Test-AgentRelayMergeBlocked -CurrentTask 'task-1' -Repository $mergeRoot -PullRequestHead $mergeHead -SchemaPath $schemaPath)) { throw 'Other task record must fail closed' }
    git -C $mergeRoot rm -q acceptance/task-1.v1.json
    git -C $mergeRoot commit -qm delete-acceptance-record
    $mergeHead = git -C $mergeRoot rev-parse HEAD
    if (-not (Test-AgentRelayMergeBlocked -CurrentTask 'task-1' -Repository $mergeRoot -PullRequestHead $mergeHead -SchemaPath $schemaPath)) { throw 'Deleted acceptance record must fail closed' }
} finally { Remove-Item -LiteralPath $mergeRoot -Recurse -Force -ErrorAction SilentlyContinue }
Write-Output 'PASS acceptance policy'
