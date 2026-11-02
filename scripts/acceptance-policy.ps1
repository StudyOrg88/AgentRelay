function Test-AgentRelayReviewValidity {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$ReviewedCodeCommit,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$PullRequestHead
    )

    git -C $Repository cat-file -e "$ReviewedCodeCommit^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'reviewed commit not found' } }
    git -C $Repository cat-file -e "$PullRequestHead^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'PR head not found' } }
    git -C $Repository merge-base --is-ancestor $ReviewedCodeCommit $PullRequestHead 2>$null
    if ($LASTEXITCODE -ne 0) { return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'reviewed commit is not an ancestor' } }
    if ($PullRequestHead -eq $ReviewedCodeCommit) {
        return [pscustomobject]@{ valid = $true; requires_review = $false; reason = 'head equals reviewed commit' }
    }

    $changedPaths = @(git -C $Repository diff --name-only --diff-filter=ACMRT $ReviewedCodeCommit $PullRequestHead)
    if ($LASTEXITCODE -ne 0 -or $changedPaths.Count -eq 0) {
        return [pscustomobject]@{ valid = $false; requires_review = $true; reason = 'diff unavailable or empty' }
    }
    foreach ($path in $changedPaths) {
        if ($path -notmatch '^(acceptance|docs/acceptance-records)/[A-Za-z0-9][A-Za-z0-9._-]*\.v1\.json$') {
            return [pscustomobject]@{ valid = $false; requires_review = $true; reason = "non-acceptance path changed: $path" }
        }
    }
    return [pscustomobject]@{ valid = $true; requires_review = $false; reason = 'acceptance records only' }
}

function Test-AgentRelayRecordParent {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$RecordCommit,
        [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string]$RecordCommitParent
    )
    $actual = git -C $Repository rev-parse "$RecordCommit^" 2>$null
    return $LASTEXITCODE -eq 0 -and $actual -eq $RecordCommitParent
}

function Test-AgentRelayMergeBlocked {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$CurrentTask,
        [Parameter(Mandatory)][object[]]$Items
    )

    return @($Items | Where-Object {
        $_.owner_task -eq $CurrentTask -and
        $_.merge_blocking -eq $true -and
        $_.status -ne 'passed'
    }).Count -gt 0
}
