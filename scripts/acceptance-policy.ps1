function Test-AgentRelayReviewValidity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$CurrentTask,
        [Parameter(Mandatory)][string]$SchemaPath,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ReviewedCodeCommit,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$PullRequestHead
    )

    git -C $Repository cat-file -e "$ReviewedCodeCommit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'reviewed commit not found' } }
    git -C $Repository cat-file -e "$PullRequestHead^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'PR head not found' } }
    git -C $Repository merge-base --is-ancestor $ReviewedCodeCommit $PullRequestHead 2>$null
    if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'reviewed commit is not an ancestor' } }
    $changedPaths = @(git -C $Repository diff --name-only --diff-filter=ACMRTD $ReviewedCodeCommit $PullRequestHead)
    if ($LASTEXITCODE -ne 0 -or ($changedPaths.Count -eq 0 -and $PullRequestHead -ne $ReviewedCodeCommit)) {
        return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'diff unavailable or empty' }
    }
    foreach ($path in $changedPaths) {
        if ($path -ne "acceptance/$CurrentTask.v1.json") {
            return [pscustomobject]@{ valid = $false; requires_review = $true; reason = "non-acceptance path changed: $path" }
        }
    }
    $recordPath = "acceptance/$CurrentTask.v1.json"
    $commits = @(git -C $Repository rev-list "$ReviewedCodeCommit..$PullRequestHead")
    if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'commit history unavailable' } }
    foreach ($commit in $commits) {
        $changes = @(git -C $Repository diff-tree --no-commit-id --name-status -r -m $commit -- $recordPath)
        if ($LASTEXITCODE -ne 0 -or @($changes | Where-Object { $_ -match '^D\t' }).Count -gt 0) {
            return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'acceptance record was deleted after review' }
        }
    }
    $record = Read-AgentRelayAcceptanceRecord -Repository $Repository -Commit $PullRequestHead -CurrentTask $CurrentTask -SchemaPath $SchemaPath
    if ($null -eq $record) { return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'acceptance record invalid for current task' } }
    if ($record.reviewed_code_commit -cne $ReviewedCodeCommit) { return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'record references a different reviewed code commit' } }
    $recordCommit = git -C $Repository log -1 --format=%H $PullRequestHead -- "acceptance/$CurrentTask.v1.json"
    if ($LASTEXITCODE -ne 0 -or $recordCommit -notmatch '^[0-9a-f]{40}$' -or -not (Test-AgentRelayRecordParent -Repository $Repository -RecordCommit $recordCommit -RecordCommitParent $record.record_commit_parent)) {
        return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'record parent does not match record commit' }
    }
    return [pscustomobject]@{ valid = $true; requires_review = $false; reason = 'acceptance records only' }
}

function Read-AgentRelayAcceptanceRecord {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')][string]$CurrentTask,
        [Parameter(Mandatory)][string]$SchemaPath
    )
    $json = (git -C $Repository show "$Commit`:acceptance/$CurrentTask.v1.json" 2>$null) -join "`n"
    if ($LASTEXITCODE -ne 0) { return $null }
    try {
        if (-not (Test-Json -Json $json -Schema (Get-Content $SchemaPath -Raw))) { return $null }
        $record = $json | ConvertFrom-Json -ErrorAction Stop
    } catch { return $null }
    if ($record.task_id -ne $CurrentTask) { return $null }
    return $record
}

function Test-AgentRelayRecordParent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$RecordCommit,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$RecordCommitParent
    )
    $resolved = git -C $Repository rev-parse --verify "$RecordCommit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0 -or $resolved -cne $RecordCommit) { return $false }
    $actual = git -C $Repository rev-parse --verify "$RecordCommit^1" 2>$null
    return $LASTEXITCODE -eq 0 -and $actual -ceq $RecordCommitParent
}

function Test-AgentRelayMergeBlocked {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$PullRequestHead,
        [Parameter(Mandatory)][string]$CurrentTask,
        [Parameter(Mandatory)][string]$SchemaPath
    )

    try {
        if ($CurrentTask -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]*$') { return $true }
        $record = Read-AgentRelayAcceptanceRecord -Repository $Repository -Commit $PullRequestHead -CurrentTask $CurrentTask -SchemaPath $SchemaPath
    } catch { return $true }
    if ($null -eq $record) { return $true }
    $reviewState = Test-AgentRelayReviewValidity -Repository $Repository -CurrentTask $CurrentTask -SchemaPath $SchemaPath -ReviewedCodeCommit $record.reviewed_code_commit -PullRequestHead $PullRequestHead
    if (-not $reviewState.valid) { return $true }
    return @($record.items | Where-Object {
        $_.owner_task -eq $CurrentTask -and
        $_.merge_blocking -eq $true -and
        $_.status -ne 'passed'
    }).Count -gt 0
}
