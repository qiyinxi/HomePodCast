#Requires -Version 7
<#
.SYNOPSIS
  Makes HomePodCast releases: betas from dev, and stable releases by promoting a tested beta (docs/RELEASING.md).

.DESCRIPTION
  -Status
      The version on dev, the latest tags, whether dev's CI is green, snapshot branches still around and which betas
      can be promoted. Read-only.

  -Beta -NotesFile <md> [-Continue] [-WhatIf]
      Releases a snapshot of origin/dev: its current commit (after git fetch) becomes the next vX.Y.Z-beta.N (X.Y.Z is
      <Version> in src/HomePodCast.csproj at that commit; N follows the highest vX.Y.Z-beta.N tag here or on origin,
      also one whose build failed). Everything is read from that commit, not from the working tree, so it runs from any
      clone or worktree, whatever is checked out there. If a push run of build.yml has already passed on that commit
      (on dev, main or release/**, what the tag build's release checks accept), the commit is tagged right away.
      Otherwise it pushes the snapshot branch release/snapshot/vX.Y.Z-beta.N at the commit and waits for that branch's
      CI: it runs in a concurrency group of its own, so pushes to dev can't cancel it (2026-10-15: a push to dev
      cancelled dev's run that the beta was waiting for). Then it tags the commit, pushes the tag, waits for the tag
      build, which publishes the GitHub prerelease, checks the prerelease and its .msi and .zip, applies -NotesFile
      and deletes the snapshot branch.
      Run again after a stop (a failed or cancelled run, the network, Ctrl+C), it continues the beta of the snapshot
      branch, at the snapshot's commit, with the run already there. The branch stays until its beta is published: a
      snapshot pushed from another checkout counts as another release going on, unless -Continue takes it over.
      It also refuses while a PR to main is open or another tag build is running (one release at a time).

  -Promote vX.Y.Z-beta.N -NotesFile <md> [-WhatIf]
      Makes vX.Y.Z from exactly that beta's source. Checks that the beta was published as a prerelease by a passing
      tag build, that vX.Y.Z doesn't exist, that <Version> at the beta is X.Y.Z and that main is an ancestor of the
      beta (so merging it into main adds nothing else). Then: pushes release/X.Y.Z at the beta's commit and waits for
      its CI; opens the PR release/X.Y.Z -> main "Release X.Y.Z" and waits for its checks; merges it with a merge
      commit "Release X.Y.Z"; checks that the merge commit's tree is the beta's tree (and doesn't tag otherwise); waits
      for main's CI on it; tags it vX.Y.Z, pushes the tag, waits for the tag build, checks the release and applies
      -NotesFile. Last, it merges main back into dev (a merge commit that changes no files) and pushes dev, so that
      main stays an ancestor of dev and the next release branch is up to date with main; when this checkout is not a
      clean dev at origin/dev it prints the commands instead. Run again after an interruption, it picks up an existing
      release/X.Y.Z at the beta's commit and an open or merged PR.

  -WhatIf runs every check and prints each step it would take; it creates, merges and pushes nothing. Another
  release going on is then only a warning, so that the rest of the plan shows.

  -SelfTest checks the decisions of -Beta (snapshot names, where it starts or continues, whether CI has to run on a
  snapshot first, the hints after a failed run, other releases going on) against known cases. Offline; build.yml
  runs it on every build.

  Run it inside the repository (any clone or worktree; the current directory's repository is used) with git and an
  authenticated gh. It never force-pushes and never deletes or moves a tag; the only branch it deletes is its own
  release/snapshot/vX.Y.Z-beta.N, once that beta is published. CI enforces the same rules for every pushed tag:
  tools/release-checks.ps1.

  The steps are this file's, wherever it is run from. What builds and checks a release (build.yml,
  tools/release-checks.ps1, the installer and package scripts) runs in CI from the tagged commit's own checkout, so it
  is always the snapshot's own; -Beta warns when this copy differs from the snapshot's tools/release.ps1.

.EXAMPLE
  ./tools/release.ps1 -Status
.EXAMPLE
  ./tools/release.ps1 -Beta -NotesFile ..\notes\0.2.2-beta.5.md -WhatIf
.EXAMPLE
  ./tools/release.ps1 -Promote v0.2.2-beta.5 -NotesFile ..\notes\0.2.2.md
.EXAMPLE
  ./tools/release.ps1 -SelfTest
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Status')] [switch] $Status,
    [Parameter(ParameterSetName = 'Beta', Mandatory)] [switch] $Beta,
    # The beta to promote, e.g. v0.2.2-beta.4.
    [Parameter(ParameterSetName = 'Promote', Mandatory)] [string] $Promote,
    # Release notes for the GitHub release (Markdown, English first, then 简体中文), applied after the tag build.
    [Parameter(ParameterSetName = 'Beta')] [Parameter(ParameterSetName = 'Promote')] [string] $NotesFile,
    # Continue the beta of a snapshot branch that was pushed from another checkout (one that is gone).
    [Parameter(ParameterSetName = 'Beta')] [switch] $Continue,
    # Run the checks and print the steps without creating, merging or pushing anything.
    [Parameter(ParameterSetName = 'Beta')] [Parameter(ParameterSetName = 'Promote')] [switch] $WhatIf,
    # Check the decisions of -Beta against known cases (offline).
    [Parameter(ParameterSetName = 'SelfTest', Mandatory)] [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Remote = 'origin'
$Workflow = 'build.yml'
$Csproj = 'src/HomePodCast.csproj'
$Checklist = 'docs/device-test-checklist.zh-CN.md'
$TagPattern = '^v(?<ver>(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*))(-(?<pre>beta|rc)\.(?<n>[1-9]\d*))?$'
$BetaTagPattern = '^v(?<ver>(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*))-beta\.(?<n>[1-9]\d*)$'
# release/snapshot/vX.Y.Z-beta.N: the commit a beta is made from, while its CI runs. build.yml runs on release/**,
# each branch in a concurrency group of its own, and the release checks accept a passed run on release/**.
$SnapshotPrefix = 'release/snapshot/'
# In the git dir of the checkout that pushed a snapshot (each worktree has its own): "<branch> <commit>".
$SnapshotMarker = 'homepodcast-release-snapshot'
$RunFields = 'databaseId,status,conclusion,url,attempt,headBranch'
$PrFields = 'number,url,state,headRefOid,mergeCommit'

function Write-Step([string] $text) { Write-Host "==> $text" -ForegroundColor Cyan }
function Write-Info([string] $text) { Write-Host "    $text" }
function Write-Plan([string] $text) { Write-Host "    [WhatIf] would $text" -ForegroundColor Yellow }
function Write-Caution([string] $text) { Write-Host "    ! $text" -ForegroundColor Yellow }

# git and gh with a check of the exit code: they return stdout and throw when the command fails.
function Invoke-Git {
    $out = & git @args
    if ($LASTEXITCODE) { throw "git $($args -join ' ') failed (exit code $LASTEXITCODE)" }
    $out
}
function Invoke-Gh {
    $out = & gh @args
    if ($LASTEXITCODE) { throw "gh $($args -join ' ') failed (exit code $LASTEXITCODE)" }
    $out
}
function Invoke-GhJson { (Invoke-Gh @args) -join "`n" | ConvertFrom-Json }

# A step that changes something: done, or with -WhatIf only described.
function Invoke-Change([string] $description, [scriptblock] $action) {
    if ($WhatIf) { Write-Plan $description; return }
    Write-Info "$description..."
    & $action
}

function Get-Short([string] $sha) { $sha.Substring(0, [Math]::Min(7, $sha.Length)) }
function Get-Subject([string] $rev) {
    $s = "$(Invoke-Git log -1 --format=%s $rev)"
    if ($s.Length -gt 90) { $s.Substring(0, 89) + '…' } else { $s }
}
function Get-RevParse([string] $rev) { "$(Invoke-Git rev-parse $rev)" }

function Test-Ancestor([string] $ancestor, [string] $descendant) {
    & git merge-base --is-ancestor $ancestor $descendant
    if ($LASTEXITCODE -eq 0) { return $true }
    if ($LASTEXITCODE -eq 1) { return $false }
    throw "git merge-base --is-ancestor $ancestor $descendant failed (exit code $LASTEXITCODE)"
}

function Get-CsprojVersion([string] $rev) {
    $xml = (Invoke-Git show "${rev}:$Csproj") -join "`n"
    if ($xml -notmatch '<Version>\s*([^<\s]+)\s*</Version>') { throw "$Csproj at $rev has no <Version>" }
    $Matches[1]
}

# Files that keep a checkout from being clean; the untracked icon/ work folder doesn't count.
function Get-DirtyFile { @(Invoke-Git status --porcelain | Where-Object { $_ -and $_ -notmatch '^\?\? icon/' }) }

# Release tags (vX.Y.Z, vX.Y.Z-beta.N, vX.Y.Z-rc.N) on the remote and here, oldest first, each with the commit it
# names. The remote's commit counts when a local tag of the same name differs.
function Get-ReleaseTag {
    # (PowerShell variable names ignore case: these must not be called $remote.)
    $remoteTags = @{}
    foreach ($line in @(Invoke-Git ls-remote --tags $Remote)) {
        $sha, $ref = "$line" -split "`t"
        $name = $ref -replace '^refs/tags/', ''
        if ($name.EndsWith('^{}')) { $remoteTags[$name.Substring(0, $name.Length - 3)] = $sha }
        elseif (-not $remoteTags.ContainsKey($name)) { $remoteTags[$name] = $sha }
    }
    $localTags = @{}
    foreach ($line in @(Invoke-Git tag -l 'v*' --format='%(refname:short) %(objectname) %(*objectname)')) {
        $name, $object, $peeled = "$line" -split ' '
        $localTags[$name] = if ($peeled) { $peeled } else { $object }
    }
    $tags = foreach ($name in @($remoteTags.Keys) + @($localTags.Keys | Where-Object { -not $remoteTags.ContainsKey($_) })) {
        if ($name -notmatch $TagPattern) { continue }
        $sha = if ($remoteTags.ContainsKey($name)) { $remoteTags[$name] } else { $localTags[$name] }
        if ($remoteTags.ContainsKey($name) -and $localTags.ContainsKey($name) -and $localTags[$name] -ne $sha) {
            Write-Caution "the local tag $name names $(Get-Short $localTags[$name]), $Remote's names $(Get-Short $sha); $Remote's counts (pushed tags are never moved)"
        }
        New-ReleaseTag $name $sha $remoteTags.ContainsKey($name)
    }
    @($tags | Sort-Object SortVersion, Rank, N)
}

# A release tag as Get-ReleaseTag lists it; $name must match $TagPattern.
function New-ReleaseTag([string] $name, [string] $sha, [bool] $onRemote) {
    if ($name -notmatch $TagPattern) { throw "'$name' is not a release tag" }
    $pre = "$($Matches['pre'])"
    [pscustomobject]@{
        Name = $name
        Sha = $sha
        Version = $Matches['ver']
        SortVersion = [version]$Matches['ver']
        Pre = $pre
        N = if ($pre) { [int]$Matches['n'] } else { 0 }
        Stable = -not $pre
        Rank = if (-not $pre) { 2 } elseif ($pre -eq 'rc') { 1 } else { 0 } # beta < rc < stable
        OnRemote = $onRemote
    }
}

# Makes sure the commit is here (git show, merge-base and rev-parse need it), fetching $ref from the remote if not.
function Confirm-Commit([string] $sha, [string] $ref) {
    & git cat-file -e "$sha^{commit}" 2>$null
    if ($LASTEXITCODE) { Invoke-Git fetch --quiet $Remote $ref }
}
function Assert-Commit($tag) { Confirm-Commit $tag.Sha "refs/tags/$($tag.Name)" }

# The newest run of the build workflow for a commit from a push to a branch or a tag (the run's "branch" is then
# the tag's name), or nothing.
function Get-PushRun([string] $sha, [string] $ref) {
    @(Invoke-GhJson run list @R --workflow $Workflow --commit $sha --branch $ref --event push --limit 20 --json $RunFields) |
        Sort-Object databaseId -Descending | Select-Object -First 1
}

function Format-Run($run) {
    if (-not $run) { return 'no run' }
    $state = if ($run.status -eq 'completed') { $run.conclusion } else { $run.status }
    "$state $($run.url)"
}

# Waits for GitHub to start the run of a push that was just made.
function Wait-PushRun([string] $sha, [string] $ref, [string] $what, [int] $minutes = 3) {
    $deadline = (Get-Date).AddMinutes($minutes)
    while (-not ($run = Get-PushRun $sha $ref)) {
        if ((Get-Date) -gt $deadline) { throw "GitHub started no $Workflow run for $what within $minutes minutes: see https://github.com/$Repo/actions" }
        Write-Info "waiting for GitHub to start the CI run for $what..."
        Start-Sleep -Seconds 10
    }
    $run
}

# Waits until the run has finished and returns it; throws, with $hint, unless it passed. $hint is a string or a
# script block that gets the finished run and returns the string.
function Wait-RunSuccess($run, [string] $what, $hint) {
    if ($run.status -ne 'completed') {
        Write-Info "waiting for $what (run $($run.databaseId)): $($run.url)"
        & gh run watch $run.databaseId @R --exit-status --compact --interval 30 | Out-Host
        if ($LASTEXITCODE) { Write-Info "gh run watch exited with $LASTEXITCODE" } # the run's conclusion below says why
        $run = Invoke-GhJson run view $run.databaseId @R --json $RunFields
    }
    if ($run.status -ne 'completed') { throw "$what is still $($run.status): $($run.url)" }
    if ($run.conclusion -ne 'success') {
        $text = if ($hint -is [scriptblock]) { & $hint $run } else { "$hint" }
        throw "$what ended with $($run.conclusion): $($run.url). $text"
    }
    Write-Info "$what passed: $($run.url)"
    $run
}

# The highest N among the vX.Y.Z-beta.N tags given; 0 when there are none (a version's first beta: Measure-Object
# returns nothing for no input, and StrictMode refuses .Maximum on it).
function Get-HighestBeta([object[]] $betas) {
    if (-not $betas -or $betas.Count -eq 0) { return 0 }
    [int]($betas | Measure-Object N -Maximum).Maximum
}

# --- Snapshots for -Beta. The functions from here to Find-ReleaseInProgress only decide; -SelfTest checks them.

# The snapshot branch of a beta tag.
function Get-SnapshotBranchName([string] $tag) { "$SnapshotPrefix$tag" }

# The beta tag that a snapshot branch is for; $null when the name isn't release/snapshot/vX.Y.Z-beta.N.
function ConvertFrom-SnapshotBranch([string] $branch) {
    if (-not $branch.StartsWith($SnapshotPrefix, [StringComparison]::Ordinal)) { return $null }
    $tag = $branch.Substring($SnapshotPrefix.Length)
    if ($tag -cmatch $BetaTagPattern) { $tag } else { $null }
}

# Whether a passed push run on this branch lets a commit be tagged: what tools/release-checks.ps1 accepts (dev, main
# or release/**, never a tag build).
function Test-CountedBranch([string] $branch) { $branch -cin 'dev', 'main' -or $branch -clike 'release/*' }

# Where -Beta starts. $devSha: origin/dev's commit; $snapshots: the release/snapshot/* branches on the remote (Branch,
# Tag, Sha); $tags: as Get-ReleaseTag lists them; $marker: what this checkout's marker says it pushed ("<branch>
# <commit>" or empty); $takeOver: -Continue. Returns Mode, Sha, Tag and Branch:
#   New     no snapshot branch: release origin/dev's commit (the caller reads the version and numbers the beta);
#   Resume  a snapshot whose tag isn't pushed yet: continue that beta at the snapshot's commit, even if dev has moved;
#   Tagged  a snapshot whose tag is pushed: wait for the tag build and finish.
# Throws when the snapshot is another checkout's release (unless $takeOver), stale, or one of several.
function Resolve-BetaStart([string] $devSha, [object[]] $snapshots, [object[]] $tags, [string] $marker, [bool] $takeOver) {
    $snapshots = @($snapshots | Where-Object { $_ })
    $tags = @($tags | Where-Object { $_ })
    if ($snapshots.Count -gt 1) {
        throw ("several snapshot branches on ${Remote}: $(($snapshots | ForEach-Object Branch) -join ', '). One release at a time: " +
            "finish one (tools/release.ps1 -Status) and delete the others (git push $Remote --delete <branch>)")
    }
    if ($snapshots.Count -eq 0) {
        if ($takeOver) { throw "-Continue: there is no snapshot branch ($SnapshotPrefix*) on $Remote to continue" }
        return [pscustomobject]@{ Mode = 'New'; Sha = $devSha; Tag = $null; Branch = $null }
    }
    $s = $snapshots[0]
    $drop = "git push $Remote --delete $($s.Branch)"
    if (-not $s.Tag) { throw "$($s.Branch) is not a snapshot of a beta (${SnapshotPrefix}vX.Y.Z-beta.N): find out what it is for, delete it ($drop) and run this again" }
    if ("$marker" -ne "$($s.Branch) $($s.Sha)" -and -not $takeOver) {
        throw ("another release is going on: $($s.Branch) (at $(Get-Short $s.Sha)) was pushed from another checkout. One release at a time: " +
            "wait for it (tools/release.ps1 -Status lists it). If it is yours and that checkout is gone, continue it here with -Continue")
    }
    $tag = @($tags | Where-Object Name -eq $s.Tag) | Select-Object -First 1
    if ($tag -and $tag.Sha -ne $s.Sha) {
        throw "$($s.Branch) is at $(Get-Short $s.Sha), but the tag $($s.Tag) names $(Get-Short $tag.Sha): the snapshot is stale. Delete it ($drop) and run this again"
    }
    if ($tag -and $tag.OnRemote) { return [pscustomobject]@{ Mode = 'Tagged'; Sha = $s.Sha; Tag = $s.Tag; Branch = $s.Branch } }
    [void]($s.Tag -match $BetaTagPattern)
    $version = $Matches['ver']
    $n = [int]$Matches['n']
    if (@($tags | Where-Object { $_.Stable -and $_.Version -eq $version })) {
        throw "v$version is released already, so $($s.Branch) is stale: delete it ($drop) and run this again"
    }
    $next = 1 + (Get-HighestBeta @($tags | Where-Object { $_.Version -eq $version -and $_.Pre -eq 'beta' -and $_.Name -ne $s.Tag }))
    if ($n -ne $next) {
        throw "$($s.Branch) is for $($s.Tag), but the next beta of $version is beta.${next}: the snapshot is stale. Delete it ($drop) and run this again"
    }
    [pscustomobject]@{ Mode = 'Resume'; Sha = $s.Sha; Tag = $s.Tag; Branch = $s.Branch }
}

# What CI still needs before a commit is tagged. $runs: the push runs of the build workflow for the commit (any branch
# or tag); $branch: its snapshot branch; $branchExists: whether that is on the remote. Returns Action and Run:
#   passed  a run that the tag build's release checks accept has passed (Run): tag the commit;
#   refuse  there is no snapshot, and dev's CI failed on the commit (Run: dev's run);
#   push    push the snapshot branch (Run: dev's run, still going or cancelled, or none);
#   start   the snapshot branch is pushed, but GitHub hasn't started its run yet;
#   wait    the snapshot's run is going (Run; a re-run of it is the same run);
#   failed  the snapshot's run ended without success (Run).
function Get-CiDecision([object[]] $runs, [string] $branch, [bool] $branchExists) {
    $runs = @($runs | Where-Object { $_ } | Sort-Object databaseId -Descending)
    $passed = @($runs | Where-Object { (Test-CountedBranch $_.headBranch) -and $_.status -eq 'completed' -and $_.conclusion -eq 'success' }) | Select-Object -First 1
    if ($passed) { return [pscustomobject]@{ Action = 'passed'; Run = $passed } }
    if ($branchExists) {
        $run = @($runs | Where-Object headBranch -CEQ $branch) | Select-Object -First 1
        $action = if (-not $run) { 'start' } elseif ($run.status -ne 'completed') { 'wait' } else { 'failed' }
        return [pscustomobject]@{ Action = $action; Run = $run }
    }
    $devRun = @($runs | Where-Object headBranch -CEQ 'dev') | Select-Object -First 1
    $failed = $devRun -and $devRun.status -eq 'completed' -and $devRun.conclusion -in 'failure', 'timed_out', 'startup_failure'
    [pscustomobject]@{ Action = $(if ($failed) { 'refuse' } else { 'push' }); Run = $devRun }
}

# What to do when the run of a snapshot branch ended without success. Nothing was tagged yet, so the number is kept.
function Get-SnapshotRunHint($run, [string] $branch) {
    $again = "then run this again: it continues with $branch and that run"
    $drop = "git push $Remote --delete $branch"
    if ($run.conclusion -eq 'cancelled') {
        # Nobody pushes to a snapshot branch: someone cancelled it, or GitHub did.
        return "Nothing was tagged. Re-run it (gh run rerun $($run.databaseId) --repo $Repo), $again. To drop this snapshot instead: $drop"
    }
    ("Nothing was tagged. If it was flaky infrastructure, re-run the failed jobs (gh run rerun $($run.databaseId) --failed --repo $Repo), $again. " +
        "If the code is broken, fix it on dev, delete the snapshot ($drop) and run this again: it takes a new snapshot of dev with the same beta number")
}

# Other releases going on, one line each: an open PR to main or a promotion branch's CI running (tools/release.ps1
# -Promote), and tag builds still running, other than $ownTag's. $mainPrs: the open PRs to main (number, headRefName,
# url); $runs: recent push runs of the build workflow.
function Find-ReleaseInProgress([object[]] $mainPrs, [object[]] $runs, [string] $ownTag) {
    foreach ($pr in @($mainPrs | Where-Object { $_ })) { "PR #$($pr.number) $($pr.headRefName) -> main is open: $($pr.url)" }
    foreach ($run in @($runs | Where-Object { $_ -and $_.status -ne 'completed' })) {
        if ($run.headBranch -cmatch $TagPattern -and $run.headBranch -cne $ownTag) { "the tag build of $($run.headBranch) is $($run.status): $($run.url)" }
        elseif ($run.headBranch -cmatch '^release/\d+\.\d+\.\d+$') { "the CI of $($run.headBranch) is $($run.status) (a promotion): $($run.url)" }
    }
}

# The release/snapshot/* branches on the remote.
function Get-SnapshotBranch {
    @(foreach ($line in @(Invoke-Git ls-remote --heads $Remote "refs/heads/$SnapshotPrefix*")) {
            if (-not "$line") { continue }
            $sha, $ref = "$line" -split "`t"
            $branch = $ref -replace '^refs/heads/', ''
            [pscustomobject]@{ Branch = $branch; Tag = ConvertFrom-SnapshotBranch $branch; Sha = $sha }
        })
}

# The marker of the snapshot this checkout pushed, in its own git dir (each worktree has one): a second checkout
# running -Beta at the same time must not take over the first one's release and apply its own notes.
function Get-SnapshotMarkerPath { Join-Path "$(Invoke-Git rev-parse --absolute-git-dir)" $SnapshotMarker }
function Read-SnapshotMarker {
    $path = Get-SnapshotMarkerPath
    if (Test-Path -LiteralPath $path -PathType Leaf) { "$(Get-Content -LiteralPath $path -Raw)".Trim() } else { '' }
}
function Write-SnapshotMarker([string] $branch, [string] $sha) { Set-Content -LiteralPath (Get-SnapshotMarkerPath) -Value "$branch $sha" -Encoding utf8NoBOM }
function Remove-SnapshotMarker { Remove-Item -LiteralPath (Get-SnapshotMarkerPath) -Force -ErrorAction SilentlyContinue }

# The push runs of the build workflow for a commit, on any branch or tag.
function Get-CommitPushRun([string] $sha) {
    @(Invoke-GhJson run list @R --workflow $Workflow --commit $sha --event push --limit 50 --json $RunFields)
}

# One release at a time; with -WhatIf only a warning, so that the rest of the plan shows.
function Assert-OneRelease([string] $ownTag) {
    $prs = @(Invoke-GhJson pr list @R --base main --state open --json 'number,headRefName,url')
    $runs = @(Invoke-GhJson run list @R --workflow $Workflow --event push --limit 100 --json $RunFields)
    $busy = @(Find-ReleaseInProgress $prs $runs $ownTag)
    if (-not $busy) { Write-Info 'no other release going on: no PR to main, no promotion or other tag build running'; return }
    $text = "another release is going on: $($busy -join '; '). One release at a time: wait for it, then run this again"
    if ($WhatIf) { Write-Caution "this would stop here: $text" } else { throw $text }
}

# The steps are this file's, wherever it runs; the build and its release checks are the tagged commit's own (CI
# checks it out). A copy that differs from the snapshot's is worth a word: an old clone may lack a newer rule.
function Test-ScriptCopy([string] $sha) {
    $path = 'tools/release.ps1'
    $theirs = "$(& git rev-parse --verify --quiet "${sha}:$path")"
    if ($LASTEXITCODE -or -not $theirs) { return }
    # hash-object --path applies the checkout's line-ending conversion, as git add would.
    $mine = "$(& git hash-object "--path=$path" -- $ScriptPath)"
    if ($LASTEXITCODE -or $mine -eq $theirs) { return }
    Write-Caution "this $path ($ScriptPath) differs from the one at $(Get-Short $sha): the release steps are this copy's; the build and its release checks are the snapshot's own"
}

# Deletes the snapshot branch once its beta is published; the tag keeps the commit.
function Remove-Snapshot([string] $branch) {
    Write-Step "The snapshot branch $branch"
    Invoke-Change "delete $branch on $Remote (the tag keeps its commit)" {
        Invoke-Git push --quiet $Remote --delete "refs/heads/$branch" | Out-Host
        Remove-SnapshotMarker
        Write-Info "deleted $branch"
    }
}

# Waits until a PR's checks have finished; throws when one failed.
function Wait-PrCheck([int] $number) {
    $deadline = (Get-Date).AddMinutes(3)
    while ($true) {
        # Exit codes here are 1 while there are no checks yet and 8 while some are pending: only the list counts.
        $json = & gh pr checks $number @R --json 'name,bucket,link' 2>$null
        $checks = if ($json) { @(($json -join "`n") | ConvertFrom-Json) } else { @() }
        if ($checks) { break }
        if ((Get-Date) -gt $deadline) { throw "PR #$number has no checks after 3 minutes: see https://github.com/$Repo/pull/$number/checks" }
        Write-Info 'waiting for the PR checks to start...'
        Start-Sleep -Seconds 10
    }
    Write-Info "waiting for the checks of PR #${number}: https://github.com/$Repo/pull/$number/checks"
    & gh pr checks $number @R --watch --interval 30 | Out-Host
    if ($LASTEXITCODE) { throw "a check of PR #$number failed: https://github.com/$Repo/pull/$number/checks. Nothing was merged; if it was flaky infrastructure, re-run it and run this again" }
    # The PR's own pull_request run is registered some seconds after the PR opens. Until then GitHub calls the PR clean on
    # the push run of the same commit (the same check names), and the merge meets the new run: 2026-10-28, Release 1.3.0
    # was refused "the base branch policy prohibits the merge" while it ran. So wait for that run to exist and pass.
    $head = (Invoke-GhJson pr view $number @R --json headRefOid).headRefOid
    $deadline = (Get-Date).AddMinutes(5)
    while ($true) {
        $runs = @(Invoke-GhJson run list @R --workflow build.yml --event pull_request --commit $head --json 'databaseId,status,conclusion')
        if ($runs) { break }
        if ((Get-Date) -gt $deadline) { throw "PR #$number has no pull_request run of build.yml after 5 minutes: see https://github.com/$Repo/pull/$number/checks. Nothing was merged" }
        Write-Info "waiting for the PR's own CI run to start..."
        Start-Sleep -Seconds 15
    }
    $run = $runs | Sort-Object databaseId -Descending | Select-Object -First 1
    if ($run.status -ne 'completed') {
        Write-Info "waiting for the PR's own CI run: https://github.com/$Repo/actions/runs/$($run.databaseId)"
        & gh run watch $run.databaseId @R --interval 30 | Out-Host
        $run = Invoke-GhJson run view $run.databaseId @R --json 'databaseId,status,conclusion'
    }
    if ($run.conclusion -ne 'success') { throw "the PR's own CI run ended with $($run.conclusion): https://github.com/$Repo/actions/runs/$($run.databaseId). Nothing was merged; if it was flaky infrastructure, re-run it and run this again" }
    # gh pr checks can call it passed before GitHub lists every run of a required check (Release 0.2.2: a second
    # build of PR #13 was still pending and the merge was refused), so merge only once GitHub says the PR is clean.
    $deadline = (Get-Date).AddMinutes(20)
    while ($true) {
        $state = (Invoke-GhJson pr view $number @R --json mergeStateStatus).mergeStateStatus
        if ($state -in 'CLEAN', 'HAS_HOOKS') { break }
        if ($state -in 'DIRTY', 'BEHIND', 'UNSTABLE') { throw "PR #$number can't be merged ($state): https://github.com/$Repo/pull/$number. Nothing was merged" }
        if ((Get-Date) -gt $deadline) { throw "PR #$number is still $state after 20 minutes: https://github.com/$Repo/pull/$number/checks. Nothing was merged" }
        Write-Info "GitHub says PR #$number is $($state.ToLower()); waiting for its checks again..."
        Start-Sleep -Seconds 15
        & gh pr checks $number @R --watch --interval 30 | Out-Host
        if ($LASTEXITCODE) { throw "a check of PR #$number failed: https://github.com/$Repo/pull/$number/checks. Nothing was merged; if it was flaky infrastructure, re-run it and run this again" }
    }
    Write-Info 'the PR checks passed and GitHub says it can be merged'
}

# Shared with the in-app notes reader: one exact language boundary, outside fenced code.
function Assert-ReleaseNotes([string] $text) {
    $markers = [regex]::Matches($text, '(?m)^## 简体中文\r?$')
    if ($markers.Count -ne 1) { throw "release notes need exactly one '## 简体中文' line (English first, then Simplified Chinese)" }
    $english = $text.Substring(0, $markers[0].Index).Trim()
    $chinese = $text.Substring($markers[0].Index + $markers[0].Length).Trim()
    if ($english -notmatch '[A-Za-z]{3,}\W+[A-Za-z]{3,}' -or $chinese -notmatch '\p{IsCJKUnifiedIdeographs}') {
        throw 'release notes need non-empty English and Simplified Chinese sections'
    }
    $fenceType = ''; $fenceLength = 0
    foreach ($line in ($english -split '\r?\n')) {
        if ($line -match '^ {0,3}(?<fence>`{3,}|~{3,})(?<tail>.*)$') {
            $run = $Matches['fence']; $tail = $Matches['tail']
            if (-not $fenceType) { $fenceType = $run.Substring(0, 1); $fenceLength = $run.Length }
            elseif ($run.StartsWith($fenceType) -and $run.Length -ge $fenceLength -and -not $tail.Trim()) { $fenceType = ''; $fenceLength = 0 }
        }
    }
    if ($fenceType) { throw 'the language boundary must not be inside a fenced code block' }
}

function Resolve-NotesFile([string] $path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "notes file '$path' not found" }
    $full = (Resolve-Path -LiteralPath $path).Path
    $text = Get-Content -LiteralPath $full -Raw -Encoding utf8
    if (-not $text -or -not $text.Trim()) { throw "notes file '$full' is empty" }
    Assert-ReleaseNotes $text
    $full
}

function New-Check([bool] $ok, [string] $text) { [pscustomobject]@{ Ok = $ok; Text = $text } }

# What decides whether a beta can become the stable release of its version. $mainSha: main's commit, which must be
# an ancestor of the beta (skipped when empty, e.g. once the release PR is merged).
function Test-Promotable($beta, $tags, $releases, [string] $mainSha) {
    $stable = "v$($beta.Version)"
    if (@($tags | Where-Object Name -eq $stable)) { New-Check $false "$stable already exists" }
    else { New-Check $true "$stable doesn't exist yet" }

    $url = "https://github.com/$Repo/releases/tag/$($beta.Name)"
    $release = @($releases | Where-Object tagName -eq $beta.Name) | Select-Object -First 1
    if (-not $release) { New-Check $false "$($beta.Name) has no GitHub release (its tag build failed or never published one)" }
    elseif ($release.isDraft) { New-Check $false "the GitHub release of $($beta.Name) is a draft: $url" }
    elseif (-not $release.isPrerelease) { New-Check $false "the GitHub release of $($beta.Name) is not marked as a prerelease: $url" }
    else { New-Check $true "published as a prerelease: $url" }

    $run = Get-PushRun $beta.Sha $beta.Name
    if (-not $run) { New-Check $false "no tag build of $($beta.Name) found" }
    elseif ($run.status -ne 'completed') { New-Check $false "the tag build of $($beta.Name) is still $($run.status): $($run.url)" }
    elseif ($run.conclusion -ne 'success') { New-Check $false "the tag build of $($beta.Name) ended with $($run.conclusion): $($run.url)" }
    else { New-Check $true "its tag build passed: $($run.url)" }

    Assert-Commit $beta
    $csprojVersion = Get-CsprojVersion $beta.Sha
    if ($csprojVersion -ne $beta.Version) { New-Check $false "$Csproj at $(Get-Short $beta.Sha) says <Version>$csprojVersion</Version>, not $($beta.Version)" }
    else { New-Check $true "<Version>$csprojVersion</Version> in $Csproj" }

    if ($mainSha) {
        if (Test-Ancestor $mainSha $beta.Sha) {
            New-Check $true "main ($(Get-Short $mainSha)) is an ancestor of it, so the merge into main has exactly its files"
        }
        else {
            $missing = @(Invoke-Git log --format='%h %s' "$($beta.Sha)..$mainSha" | Select-Object -First 5)
            New-Check $false ("main ($(Get-Short $mainSha)) has commits that $($beta.Name) doesn't (" + ($missing -join '; ') + "). " +
                "main's branch protection wants release/$($beta.Version) up to date with main, and the merge would put them into the stable release: " +
                "merge main into dev, make a new beta from that, test it and promote that one")
        }
    }
}

function Show-Status {
    Invoke-Git fetch --quiet $Remote
    $tags = Get-ReleaseTag
    $releases = @(Invoke-GhJson release list @R --limit 100 --json 'tagName,isDraft,isPrerelease,publishedAt')
    $dev = Get-RevParse "$Remote/dev"
    $main = Get-RevParse "$Remote/main"
    $version = Get-CsprojVersion $dev
    $devRun = Get-PushRun $dev 'dev'

    Write-Step "HomePodCast releases ($Repo)"
    $devTags = @($tags | Where-Object Sha -eq $dev | ForEach-Object Name)
    $mainTags = @($tags | Where-Object Sha -eq $main | ForEach-Object Name)
    Write-Info "dev    $(Get-Short $dev)  <Version>$version</Version>$(if ($devTags) { "  tagged $($devTags -join ', ')" })  $(Get-Subject $dev)"
    Write-Info "       dev's CI on it: $(Format-Run $devRun)"
    Write-Info "main   $(Get-Short $main)$(if ($mainTags) { "  tagged $($mainTags -join ', ')" })  $(Get-Subject $main)"
    Write-Info "       main is an ancestor of dev: $(if (Test-Ancestor $main $dev) { 'yes' } else { "no: merge main into dev (git merge $Remote/main) before the next beta that is to be promoted" })"
    $latestStable = @($tags | Where-Object Stable) | Select-Object -Last 1
    $latestPre = @($tags | Where-Object { -not $_.Stable }) | Select-Object -Last 1
    Write-Info "latest release:    $(if ($latestStable) { "$($latestStable.Name) ($(Get-Short $latestStable.Sha))" } else { 'none' })"
    Write-Info "latest beta tag:   $(if ($latestPre) { "$($latestPre.Name) ($(Get-Short $latestPre.Sha))" } else { 'none' })"
    $branch = "$(Invoke-Git branch --show-current)"
    $head = Get-RevParse HEAD
    $where = if ($head -eq $dev) { "= $Remote/dev" } else { "not $Remote/dev" }
    Write-Info "this checkout:     $(if ($branch) { $branch } else { 'detached HEAD' }) at $(Get-Short $head) ($where)"

    Write-Step "Snapshot branches ($SnapshotPrefix*)"
    $snapshots = @(Get-SnapshotBranch)
    $marker = Read-SnapshotMarker
    if (-not $snapshots) { Write-Info 'none: no beta is being made with tools/release.ps1 -Beta' }
    foreach ($s in $snapshots) {
        $tag = if ($s.Tag) { @($tags | Where-Object Name -eq $s.Tag) | Select-Object -First 1 }
        $state = if (-not $s.Tag) { 'not a snapshot of a beta: find out what it is for' }
        elseif ($tag -and $tag.OnRemote) { "$($s.Tag) is pushed; its tag build: $(Format-Run (Get-PushRun $s.Sha $s.Tag))" }
        else { "$($s.Tag) is not tagged yet" }
        $owner = if ($marker -eq "$($s.Branch) $($s.Sha)") { 'pushed from this checkout' } else { 'pushed from another checkout' }
        Confirm-Commit $s.Sha "refs/heads/$($s.Branch)"
        Write-Info "$($s.Branch)  $(Get-Short $s.Sha)  $owner  $(Get-Subject $s.Sha)"
        Write-Info "       its CI: $(Format-Run (Get-PushRun $s.Sha $s.Branch))"
        Write-Info "       $state"
    }

    Write-Step 'Betas without a stable release'
    $promotable = @() # the newest promotable beta of each version
    $open = @($tags | Where-Object { -not $_.Stable } | ForEach-Object Version | Select-Object -Unique |
        Where-Object { $v = $_; -not @($tags | Where-Object { $_.Stable -and $_.Version -eq $v }) })
    if (-not $open) { Write-Info 'none: every version with betas has its stable release' }
    foreach ($v in $open) {
        $newest = $null
        foreach ($b in @($tags | Where-Object { -not $_.Stable -and $_.Version -eq $v }) | Sort-Object Rank, N -Descending) {
            $failed = @(Test-Promotable $b $tags $releases $main | Where-Object { -not $_.Ok })
            if ($failed) { Write-Info ("{0,-16} {1}  not promotable: {2}" -f $b.Name, (Get-Short $b.Sha), (($failed | ForEach-Object Text) -join '; ')) }
            else {
                Write-Info ("{0,-16} {1}  promotable" -f $b.Name, (Get-Short $b.Sha))
                if (-not $newest) { $newest = $b; $promotable += $b }
            }
        }
    }

    Write-Step 'Next'
    $betas = @($tags | Where-Object { $_.Version -eq $version -and $_.Pre -eq 'beta' })
    $next = "v$version-beta.$(1 + (Get-HighestBeta $betas))"
    $ci = Get-CiDecision (Get-CommitPushRun $dev) (Get-SnapshotBranchName $next) $false
    if ($snapshots) {
        $s = $snapshots[0]
        Write-Info "tools/release.ps1 -Beta continues $(if ($s.Tag) { $s.Tag } else { $s.Branch }) from $($s.Branch)$(if ($marker -ne "$($s.Branch) $($s.Sha)") { ' (from another checkout: wait for it, or take it over with -Continue)' })"
    }
    elseif (@($tags | Where-Object { $_.Stable -and $_.Version -eq $version })) {
        Write-Info "v$version is released: change <Version> in $Csproj on dev (a commit 'Version X.Y.Z') before the next beta"
    }
    elseif ($devTags) { Write-Info "dev's HEAD is already $($devTags -join ', '): the next beta ($next) needs new commits on dev" }
    elseif ($ci.Action -eq 'refuse') { Write-Info "dev's CI failed on its HEAD: fix it before the next beta ($next)" }
    elseif ($ci.Action -eq 'passed') { Write-Info "tools/release.ps1 -Beta tags dev's HEAD as $next right away (its CI passed on $($ci.Run.headBranch))" }
    else { Write-Info "tools/release.ps1 -Beta releases dev's HEAD as ${next}: CI runs on $(Get-SnapshotBranchName $next) first, then it is tagged" }
    foreach ($b in $promotable) {
        Write-Info "tools/release.ps1 -Promote $($b.Name) makes v$($b.Version), once $Checklist is done with it on the real HomePod"
    }
}

function Invoke-Beta {
    Write-Step 'The snapshot'
    Invoke-Git fetch --quiet $Remote
    $dev = Get-RevParse "$Remote/dev"
    $tags = Get-ReleaseTag
    $start = Resolve-BetaStart $dev @(Get-SnapshotBranch) $tags (Read-SnapshotMarker) $Continue.IsPresent
    $sha = $start.Sha
    if ($start.Mode -eq 'New') { Write-Info "$Remote/dev at $(Get-Short $sha): $(Get-Subject $sha)" }
    else {
        Confirm-Commit $sha "refs/heads/$($start.Branch)"
        Write-Info "$($start.Branch) at $(Get-Short $sha): continuing $($start.Tag)$(if ($start.Mode -eq 'Tagged') { ', whose tag is pushed' }): $(Get-Subject $sha)"
        if ($Continue) { Invoke-Change "mark $($start.Branch) as this checkout's release" { Write-SnapshotMarker $start.Branch $sha } }
        $newer = if ($sha -ne $dev) { [int]"$(Invoke-Git rev-list --count "$sha..$dev")" } else { 0 }
        if ($newer) {
            Write-Info ("$Remote/dev has $newer newer commit(s); they go into the next beta" +
                $(if ($start.Mode -eq 'Resume') { ". To put them into this one, delete the snapshot first (git push $Remote --delete $($start.Branch)) and run this again" }))
        }
    }
    Test-ScriptCopy $sha

    if ($start.Mode -eq 'Tagged') {
        Write-Step 'Other releases'
        Assert-OneRelease $start.Tag
        if ($NotesFile) { Write-Info "notes: $NotesFile" }
        Wait-TagBuild $start.Tag $sha $start.Branch
        Complete-Release $start.Tag $true
        Remove-Snapshot $start.Branch
        return
    }

    # Everything from the commit itself (git show), not from this working tree.
    $version = Get-CsprojVersion $sha
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "<Version> in $Csproj at $(Get-Short $sha) is '$version', not X.Y.Z" }
    Write-Info "version $version (<Version> in $Csproj at $(Get-Short $sha))"
    if (@($tags | Where-Object Name -eq "v$version")) { throw "v$version is already released. Change <Version> in $Csproj on dev first (a commit of its own, 'Version X.Y.Z'), push it, then run this again" }
    $latestStable = @($tags | Where-Object Stable) | Select-Object -Last 1
    if ($latestStable -and [version]$version -lt $latestStable.SortVersion) { throw "version $version is lower than the latest release $($latestStable.Name)" }
    $betas = @($tags | Where-Object { $_.Version -eq $version -and $_.Pre -eq 'beta' -and $_.Name -ne $start.Tag })
    if ($start.Mode -eq 'Resume') {
        $tag = $start.Tag
        if (-not $tag.StartsWith("v$version-beta.", [StringComparison]::Ordinal)) {
            throw "$($start.Branch) is for $tag, but $Csproj at its commit says <Version>$version</Version>: delete it (git push $Remote --delete $($start.Branch)) and run this again"
        }
    }
    else { $tag = "v$version-beta.$(1 + (Get-HighestBeta $betas))" }
    $n = [int]($tag -replace '^.*-beta\.', '')
    $what = if ($start.Mode -eq 'New') { "$Remote/dev's commit" } else { 'the snapshot' }
    $onSha = @($tags | Where-Object { $_.Sha -eq $sha -and $_.Name -ne $tag } | ForEach-Object Name)
    if ($onSha) {
        throw ("$what ($(Get-Short $sha)) is already tagged $($onSha -join ', '). Tags are never moved or doubled: if a tag build failed on flaky infrastructure, " +
            "re-run its failed jobs (gh run rerun <run id> --failed) and finish by hand (docs/RELEASING.md); a new beta needs new commits on dev")
    }
    Write-Info "next beta: $tag$(if ($betas) { " (after $(($betas | ForEach-Object Name) -join ', '))" })"
    $main = Get-RevParse "$Remote/main"
    if (-not (Test-Ancestor $main $sha)) {
        Write-Caution "main ($(Get-Short $main)) is not an ancestor of $what, so $tag couldn't be promoted to v${version}: if it is meant to be, merge main into dev first (git merge $Remote/main)"
    }
    if ($NotesFile) { Write-Info "notes: $NotesFile" }
    Write-Step 'Other releases'
    Assert-OneRelease $tag

    $branch = Get-SnapshotBranchName $tag
    $snapshot = $start.Mode -eq 'Resume' # a snapshot branch to delete once the beta is published
    Write-Step "CI on $(Get-Short $sha)"
    $ci = Get-CiDecision (Get-CommitPushRun $sha) $branch $snapshot
    $hint = { param($run) Get-SnapshotRunHint $run $branch }
    switch ($ci.Action) {
        'passed' {
            Write-Info "CI passed on $($ci.Run.headBranch) for this commit: $($ci.Run.url)"
            if (-not $snapshot) { Write-Info 'so it is tagged right away, without a snapshot branch' }
        }
        'refuse' {
            throw ("dev's CI failed on $(Get-Short $sha): $($ci.Run.url). Fix it on dev. If it was flaky infrastructure, re-run its failed jobs " +
                "(gh run rerun $($ci.Run.databaseId) --failed --repo $Repo) and run this again; should a push to dev cancel that re-run, this makes a snapshot instead")
        }
        'failed' { throw "the CI of $branch ended with $($ci.Run.conclusion): $($ci.Run.url). $(Get-SnapshotRunHint $ci.Run $branch)" }
        'push' {
            $devCi = if (-not $ci.Run) { 'dev has no CI run for it' }
            else { "dev's CI on it is $(if ($ci.Run.status -eq 'completed') { $ci.Run.conclusion } else { $ci.Run.status }) ($($ci.Run.url))" }
            Write-Info "${devCi}: CI runs on a snapshot branch, where pushes to dev can't cancel it"
            Invoke-Change "push $(Get-Short $sha) to $Remote as $branch" {
                Invoke-Git push --quiet $Remote "${sha}:refs/heads/$branch" | Out-Host
                Write-SnapshotMarker $branch $sha
            }
            $snapshot = $true
            if ($WhatIf) { Write-Plan "wait for the CI of $branch (a concurrency group of its own: pushes to dev don't cancel it)" }
            else { [void](Wait-RunSuccess (Wait-PushRun $sha $branch "the push to $branch") "the CI of $branch" $hint) }
        }
        default {
            # start or wait: the snapshot is pushed already; a re-run of its failed jobs is the same run.
            $run = if ($ci.Run) { $ci.Run } else { Wait-PushRun $sha $branch "the push to $branch" }
            if ($WhatIf -and $run.status -ne 'completed') { Write-Plan "wait for the CI of $branch (run $($run.databaseId), $($run.status)): $($run.url)" }
            else { [void](Wait-RunSuccess $run "the CI of $branch" $hint) }
        }
    }

    Write-Step "Tag $tag"
    $local = "$(& git rev-parse --verify --quiet "refs/tags/$tag^{commit}")"
    if ($local -eq $sha) { Write-Info "the tag $tag is here already, on $(Get-Short $sha)" }
    else { Invoke-Change "create the annotated tag $tag on $(Get-Short $sha) ('HomePodCast $version beta $n')" { Invoke-Git tag -a $tag -m "HomePodCast $version beta $n" $sha } }
    Invoke-Change "push $tag to $Remote" { Invoke-Git push $Remote "refs/tags/$tag" | Out-Host }
    Wait-TagBuild $tag $sha $(if ($snapshot) { $branch } else { '' })
    Complete-Release $tag $true
    if ($snapshot) { Remove-Snapshot $branch }
}

# $snapshot: the beta's snapshot branch, if it has one; it stays when the tag build fails, so that running -Beta
# again finishes this beta.
function Wait-TagBuild([string] $tag, [string] $sha, [string] $snapshot) {
    Write-Step "The tag build of $tag"
    if ($WhatIf) { Write-Plan "wait for the build of $tag (release checks, build, then the GitHub release)"; return }
    $run = Wait-PushRun $sha $tag "the push of $tag"
    $hint = if ($snapshot) {
        "The tag stays: a pushed tag is never moved or deleted. $snapshot stays too until the beta is published. If it failed on flaky infrastructure, re-run the failed jobs " +
        "(gh run rerun $($run.databaseId) --failed --repo $Repo), then run this again: it waits for the tag build, checks the release, applies the notes and deletes $snapshot. " +
        "If the code is broken, delete the snapshot (git push $Remote --delete $snapshot), fix it on dev and make the next beta."
    }
    else {
        "The tag stays: a pushed tag is never moved or deleted. If it failed on flaky infrastructure, re-run the failed jobs " +
        "(gh run rerun $($run.databaseId) --failed --repo $Repo) and finish by hand (docs/RELEASING.md); if the code is broken, fix it on dev and make the next beta."
    }
    [void](Wait-RunSuccess $run "the build of $tag" $hint)
}

# Checks the GitHub release that the tag build published and applies the notes.
function Complete-Release([string] $tag, [bool] $prerelease) {
    $kind = if ($prerelease) { 'a prerelease' } else { 'a release (not a prerelease)' }
    Write-Step "The GitHub release $tag"
    if ($WhatIf) {
        Write-Plan "check that $tag is published as $kind with the .msi and the -win-x64.zip"
        if ($NotesFile) { Write-Plan "apply the notes: gh release edit $tag --notes-file $NotesFile" }
        return
    }
    $release = Invoke-GhJson release view $tag @R --json 'url,isDraft,isPrerelease,assets'
    $assets = @($release.assets | ForEach-Object name)
    $problems = @(
        if ($release.isDraft) { 'it is a draft' }
        if ($release.isPrerelease -ne $prerelease) { "it is $(if ($release.isPrerelease) { 'a prerelease' } else { 'not a prerelease' })" }
        if (-not ($assets -like '*.msi')) { 'it has no .msi' }
        if (-not ($assets -like '*-win-x64.zip')) { 'it has no -win-x64.zip' }
    )
    if ($problems) { throw "the release $tag is not right: $($problems -join '; '): $($release.url)" }
    Write-Info "published as ${kind}: $($release.url)"
    Write-Info "assets: $($assets -join ', ')"
    if ($NotesFile) {
        Invoke-Gh release edit $tag @R --notes-file $NotesFile | Out-Null
        Write-Info "notes applied from $NotesFile"
    }
    else {
        Write-Caution "the notes are GitHub's generated list: write them (English first, then 简体中文) and apply them with gh release edit $tag --notes-file <file>"
    }
}

function Invoke-Promote([string] $betaTag) {
    if ($betaTag -notmatch $TagPattern -or -not $Matches['pre']) { throw "-Promote takes the beta to promote, like v0.2.2-beta.4 (not '$betaTag')" }
    $version = $Matches['ver']
    $stable = "v$version"
    $branch = "release/$version"
    $title = "Release $version"

    Write-Step "Checking $betaTag"
    Invoke-Git fetch --quiet $Remote
    $tags = Get-ReleaseTag
    $beta = @($tags | Where-Object Name -eq $betaTag) | Select-Object -First 1
    if (-not $beta -or -not $beta.OnRemote) { throw "$betaTag is not a tag on $Remote" }
    Assert-Commit $beta
    $betaTree = Get-RevParse "$($beta.Sha)^{tree}"
    Write-Info "$betaTag = $(Get-Short $beta.Sha) (tree $(Get-Short $betaTree)): $(Get-Subject $beta.Sha)"
    $main = Get-RevParse "$Remote/main"
    $prs = @(Invoke-GhJson pr list @R --head $branch --base main --state all --json $PrFields)
    $pr = @($prs | Where-Object state -eq 'MERGED') + @($prs | Where-Object state -eq 'OPEN') | Select-Object -First 1
    $merged = $pr -and $pr.state -eq 'MERGED'
    if ($pr -and $pr.headRefOid -ne $beta.Sha) { throw "PR #$($pr.number) ($branch -> main, $($pr.state.ToLower())) has head $(Get-Short $pr.headRefOid), not $betaTag ($(Get-Short $beta.Sha)): sort it out by hand ($($pr.url))" }
    $releases = @(Invoke-GhJson release list @R --limit 100 --json 'tagName,isDraft,isPrerelease')
    # Once the PR is merged main contains the beta's merge commit, so main's ancestry is checked on that instead.
    $checks = @(Test-Promotable $beta $tags $releases $(if ($merged) { '' } else { $main }))
    foreach ($c in $checks) { if ($c.Ok) { Write-Info "ok: $($c.Text)" } else { Write-Host "    no: $($c.Text)" -ForegroundColor Red } }
    if (@($checks | Where-Object { -not $_.Ok })) { throw "$betaTag can't be promoted (see above)" }
    if ($NotesFile) { Write-Info "notes: $NotesFile" }
    $newer = @($tags | Where-Object { -not $_.Stable -and $_.Version -eq $version -and ($_.Rank -gt $beta.Rank -or ($_.Rank -eq $beta.Rank -and $_.N -gt $beta.N)) } | ForEach-Object Name)
    if ($newer) { Write-Caution "newer prereleases of $version exist ($($newer -join ', ')): $stable gets none of their changes" }
    Write-Caution "promote only a beta that passed $Checklist on the real HomePod (the PR lists it)"
    if ([int]$version.Split('.')[0] -ge 1) { Write-Caution "$stable is a 1.x release: the project owner says yes to it first (docs/RELEASING.md)" }

    Write-Step "Branch $branch at $betaTag"
    $existing = "$(Invoke-Git ls-remote --heads $Remote "refs/heads/$branch")" -split "`t" | Select-Object -First 1
    if ($existing) {
        if ($existing -ne $beta.Sha) { throw "$branch already exists on $Remote at $(Get-Short $existing), not at $betaTag ($(Get-Short $beta.Sha)). Branches are never moved: find out what it is for and sort it out by hand" }
        Write-Info "$branch is already on $Remote at $(Get-Short $beta.Sha)"
    }
    else {
        Invoke-Change "push $(Get-Short $beta.Sha) to $Remote as $branch" { Invoke-Git push $Remote "$($beta.Sha):refs/heads/$branch" | Out-Host }
    }
    if ($merged) { Write-Info "PR #$($pr.number) is merged already: $($pr.url)" }
    elseif ($WhatIf -and -not $existing) { Write-Plan "wait for the CI of the push to $branch" }
    else {
        $run = Wait-PushRun $beta.Sha $branch "the push to $branch"
        if ($WhatIf -and $run.status -ne 'completed') { Write-Plan "wait for the CI of $branch (run $($run.databaseId)): $($run.url)" }
        else { [void](Wait-RunSuccess $run "the CI of $branch" 'Nothing was merged; if it was flaky infrastructure, re-run the failed jobs and run this again.') }
    }

    Write-Step "PR $branch -> main"
    if ($pr) { Write-Info "PR #$($pr.number) ($($pr.state.ToLower())): $($pr.url)" }
    else {
        $betaUrl = "https://github.com/$Repo/releases/tag/$betaTag"
        $body = @"
Promotes [$betaTag]($betaUrl) to ${stable}: the same source (commit $($beta.Sha)), no new code.

- [ ] ``$Checklist`` done on the real HomePod with $betaTag

Made by ``tools/release.ps1 -Promote $betaTag`` ([docs/RELEASING.md](docs/RELEASING.md)): it merges this PR with a merge commit "$title", checks that main's tree is then exactly the beta's, waits for main's CI and tags $stable on the merge commit.
"@
        if ($WhatIf) { Write-Plan "open the PR $branch -> main '$title' (body: $betaTag and its release, the device checklist)" }
        else {
            $bodyFile = Join-Path ([System.IO.Path]::GetTempPath()) "homepodcast-pr-$version.md"
            Set-Content -LiteralPath $bodyFile -Value $body -Encoding utf8NoBOM
            try { $url = @(Invoke-Gh pr create @R --base main --head $branch --title $title --body-file $bodyFile)[-1] }
            finally { Remove-Item -LiteralPath $bodyFile -ErrorAction SilentlyContinue }
            $pr = Invoke-GhJson pr view "$url" @R --json $PrFields
            Write-Info "opened PR #$($pr.number): $($pr.url)"
        }
    }

    $merge = $null
    if (-not $merged) {
        Write-Step "Checks and merge of the PR"
        if ($WhatIf) {
            Write-Plan "wait for the PR's checks"
            Write-Plan "merge it with a merge commit '$title' (gh pr merge --merge --subject '$title' --body '$title' --match-head-commit $(Get-Short $beta.Sha))"
        }
        else {
            Wait-PrCheck $pr.number
            Invoke-Gh pr merge $pr.number @R --merge --subject $title --body $title --match-head-commit $beta.Sha | Out-Host
            $pr = Invoke-GhJson pr view $pr.number @R --json $PrFields
            if ($pr.state -ne 'MERGED') { throw "PR #$($pr.number) is $($pr.state.ToLower()) after gh pr merge: $($pr.url)" }
        }
    }

    Write-Step "main's merge commit"
    if ($WhatIf) {
        Write-Plan "check that the merge commit's tree is $betaTag's tree ($(Get-Short $betaTree)) and refuse to tag it otherwise"
        Write-Plan "wait for main's CI on the merge commit"
    }
    else {
        $merge = "$($pr.mergeCommit.oid)"
        Invoke-Git fetch --quiet $Remote
        $parents = @("$(Invoke-Git rev-list --parents -n 1 $merge)" -split ' ' | Select-Object -Skip 1)
        $mergeTree = Get-RevParse "$merge^{tree}"
        Write-Info "$(Get-Short $merge) '$(Get-Subject $merge)', parents $(($parents | ForEach-Object { Get-Short $_ }) -join ' + '), tree $(Get-Short $mergeTree)"
        if ($mergeTree -ne $betaTree) {
            throw ("the merge commit $(Get-Short $merge) on main has tree $(Get-Short $mergeTree), $betaTag has $(Get-Short $betaTree): main had changes that the beta doesn't. " +
                "Not tagging it: $stable must be exactly the source of a tested beta. Merge main into dev, make a new beta from that, test it and promote that one")
        }
        if ($parents.Count -ne 2 -or $parents[1] -ne $beta.Sha) { throw "$(Get-Short $merge) is not a merge of $betaTag into main; not tagging it" }
        if (-not (Test-Ancestor $merge "$Remote/main")) { throw "$(Get-Short $merge) is not on $Remote/main; not tagging it" }
        Write-Info "same tree as $betaTag"
        $run = Wait-PushRun $merge 'main' 'the merge into main'
        [void](Wait-RunSuccess $run "main's CI" "$stable is not tagged. If it was flaky infrastructure, re-run the failed jobs and run this again; otherwise fix it on dev and make a new beta.")
    }

    Write-Step "Tag $stable"
    Invoke-Change "create the annotated tag $stable on $(if ($merge) { Get-Short $merge } else { 'the merge commit' }) ('HomePodCast $version')" { Invoke-Git tag -a $stable -m "HomePodCast $version" $merge }
    Invoke-Change "push $stable to $Remote" { Invoke-Git push $Remote "refs/tags/$stable" | Out-Host }
    Wait-TagBuild $stable $merge
    Complete-Release $stable $false
    Merge-MainIntoDev $version
}

# dev gets main's merge commit back through a merge commit that changes no files (main's tree is the beta's, and dev
# contains the beta). A fast-forward isn't possible: dev doesn't contain main's merge commit. This keeps main an
# ancestor of dev, which the next promotion needs: main's branch protection wants the release branch up to date with
# main, and the merge into main must not add anything to the beta's files.
function Merge-MainIntoDev([string] $version) {
    Write-Step 'main back into dev'
    # A short subject line, the why in the body (each -m is a paragraph of its own).
    $subject = "Merge main after Release $version"
    $body = "dev contains main's merge commit again (no file changes)."
    $manual = "git switch dev; git pull --ff-only; git merge --no-ff -m `"$subject`" -m `"$body`" $Remote/main; git push $Remote dev"
    $branch = "$(Invoke-Git branch --show-current)"
    if ($WhatIf) {
        # Behind $Remote/dev is fine: the real run fetches and fast-forwards first.
        $cleanDev = $branch -eq 'dev' -and -not (Get-DirtyFile) -and (Test-Ancestor HEAD "$Remote/dev")
        if ($cleanDev) { Write-Plan "merge $Remote/main into dev ('$subject'), check that no file changed and push dev" }
        else { Write-Plan "print the commands to merge main into dev, since this checkout is not a clean dev at $Remote/dev: $manual" }
        return
    }
    Invoke-Git fetch --quiet $Remote
    if (Test-Ancestor "$Remote/main" "$Remote/dev") { Write-Info 'dev already contains main'; return }
    # dev moves while a promotion waits on CI: that's what the snapshot branches allow (2026-10-15: a PR merged into
    # dev during the 1.1.0 promotion left this checkout behind, and dev had to be merged by hand). Catch up first.
    if ($branch -eq 'dev' -and -not (Get-DirtyFile) -and (Get-RevParse HEAD) -ne (Get-RevParse "$Remote/dev") -and
        (Test-Ancestor HEAD "$Remote/dev")) {
        Invoke-Git merge --ff-only --quiet "$Remote/dev"
        Write-Info "dev fast-forwarded to $(Get-Short (Get-RevParse HEAD))"
    }
    $cleanDev = $branch -eq 'dev' -and -not (Get-DirtyFile) -and (Get-RevParse HEAD) -eq (Get-RevParse "$Remote/dev")
    if (-not $cleanDev) { Write-Caution "dev not touched (this checkout is not a clean dev at $Remote/dev); do it by hand: $manual"; return }
    & git merge --no-ff --no-edit -m $subject -m $body "$Remote/main" | Out-Host
    if ($LASTEXITCODE) {
        & git merge --abort
        throw "merging $Remote/main into dev failed and was aborted; nothing was pushed. Do it by hand: $manual"
    }
    if ((Get-RevParse 'HEAD^{tree}') -ne (Get-RevParse 'HEAD^1^{tree}')) {
        throw "merging main changed files on dev, which it shouldn't. Nothing was pushed: look at git diff HEAD^1 HEAD, then push the merge or undo it yourself"
    }
    Invoke-Git push $Remote dev | Out-Host
    Write-Info "dev contains main again: $(Get-Short (Get-RevParse HEAD))"
}

# Checks the decisions of -Beta against known cases; touches no repository, needs no network.
function Invoke-SelfTest {
    $script:Repo = 'qiyinxi/HomePodCast'
    $script:failures = 0
    $script:count = 0
    function Test-Case([string] $label, [scriptblock] $test) {
        $script:count++
        try { $ok = [bool](& $test) } catch { $ok = $false; $label += " (threw: $($_.Exception.Message))" }
        if ($ok) { Write-Host "ok    $label" -ForegroundColor Green } else { $script:failures++; Write-Host "WRONG $label" -ForegroundColor Red }
    }
    # Passes when $action throws a message that matches $pattern.
    function Test-Throws([string] $label, [string] $pattern, [scriptblock] $action) {
        $script:count++
        try { & $action | Out-Null; $ok = $false; $label += ' (did not throw)' }
        catch { $ok = $_.Exception.Message -match $pattern; if (-not $ok) { $label += " (threw: $($_.Exception.Message))" } }
        if ($ok) { Write-Host "ok    $label" -ForegroundColor Green } else { $script:failures++; Write-Host "WRONG $label" -ForegroundColor Red }
    }
    function Run([string] $branch, [string] $status, [string] $conclusion = '', [long] $id = 1) {
        [pscustomobject]@{ databaseId = $id; status = $status; conclusion = $conclusion; headBranch = $branch; url = "https://example.invalid/runs/$id"; attempt = 1 }
    }
    function Snap([string] $branch, [string] $sha) { [pscustomobject]@{ Branch = $branch; Tag = ConvertFrom-SnapshotBranch $branch; Sha = $sha } }

    Write-Host 'Bilingual notes'
    Test-Case 'English first, one exact Chinese boundary' { Assert-ReleaseNotes "New features`n`n## 简体中文`n新功能"; $true }
    Test-Case 'CRLF notes' { Assert-ReleaseNotes "New features`r`n## 简体中文`r`n新功能"; $true }
    Test-Throws 'missing boundary is refused' 'exactly one' { Assert-ReleaseNotes "New features`n新功能" }
    Test-Throws 'duplicate boundary is refused' 'exactly one' { Assert-ReleaseNotes "New features`n## 简体中文`n新功能`n## 简体中文`n重复" }
    Test-Throws 'empty Chinese is refused' 'non-empty' { Assert-ReleaseNotes "New features`n## 简体中文`n" }
    Test-Throws 'empty English is refused' 'non-empty' { Assert-ReleaseNotes "## 简体中文`n新功能" }
    Test-Throws 'boundary in code is refused' 'fenced code' { Assert-ReleaseNotes ('New features' + "`n" + '```markdown' + "`n## 简体中文`n新功能`n" + '```') }
    Test-Throws 'boundary in tilde code is refused' 'fenced code' { Assert-ReleaseNotes "New features`n~~~markdown`n## 简体中文`n新功能`n~~~" }
    Test-Throws 'shorter fence does not close code' 'fenced code' { Assert-ReleaseNotes ('New features' + "`n" + '````markdown' + "`n" + '```' + "`n## 简体中文`n新功能") }

    $dev = 'd' * 40
    $snapSha = 'a' * 40
    $other = 'b' * 40
    $b6 = 'release/snapshot/v1.1.0-beta.6'
    $own = "$b6 $snapSha"
    $tags = @(1..5 | ForEach-Object { New-ReleaseTag "v1.1.0-beta.$_" ("$_" * 40) $true }) + @(New-ReleaseTag 'v1.0.0' ('9' * 40) $true)

    Write-Host 'Snapshot names'
    Test-Case 'the snapshot of v1.1.0-beta.6 is release/snapshot/v1.1.0-beta.6' { (Get-SnapshotBranchName 'v1.1.0-beta.6') -ceq $b6 }
    Test-Case 'release/snapshot/v1.1.0-beta.6 is for v1.1.0-beta.6' { (ConvertFrom-SnapshotBranch $b6) -ceq 'v1.1.0-beta.6' }
    Test-Case 'release/snapshot/v1.1.0-beta.12 is for v1.1.0-beta.12' { (ConvertFrom-SnapshotBranch 'release/snapshot/v1.1.0-beta.12') -ceq 'v1.1.0-beta.12' }
    Test-Case 'release/1.1.0 (a promotion) is no snapshot' { $null -eq (ConvertFrom-SnapshotBranch 'release/1.1.0') }
    Test-Case 'release/snapshot/v1.1.0 is no snapshot (stable releases are promotions)' { $null -eq (ConvertFrom-SnapshotBranch 'release/snapshot/v1.1.0') }
    Test-Case 'release/snapshot/v1.1.0-rc.1 is no snapshot' { $null -eq (ConvertFrom-SnapshotBranch 'release/snapshot/v1.1.0-rc.1') }
    Test-Case 'release/snapshot/v1.1.0-beta.06 is no snapshot (leading zero)' { $null -eq (ConvertFrom-SnapshotBranch 'release/snapshot/v1.1.0-beta.06') }
    Test-Case 'release/snapshot/V1.1.0-BETA.6 is no snapshot (case)' { $null -eq (ConvertFrom-SnapshotBranch 'release/snapshot/V1.1.0-BETA.6') }
    Test-Case 'a passed run on a snapshot branch counts, as in release-checks.ps1 (release/**)' { Test-CountedBranch $b6 }
    Test-Case 'a tag build does not count' { -not (Test-CountedBranch 'v1.1.0-beta.5') }

    Write-Host 'Where -Beta starts'
    Test-Case 'no snapshot: a new beta from origin/dev' { $r = Resolve-BetaStart $dev @() $tags '' $false; $r.Mode -eq 'New' -and $r.Sha -eq $dev }
    Test-Case "this checkout's snapshot, not tagged: continue it at its commit, though dev moved on" {
        $r = Resolve-BetaStart $dev @(Snap $b6 $snapSha) $tags $own $false; $r.Mode -eq 'Resume' -and $r.Sha -eq $snapSha -and $r.Tag -eq 'v1.1.0-beta.6' -and $r.Branch -eq $b6 }
    Test-Case 'its tag pushed already: continue with the tag build' {
        $r = Resolve-BetaStart $dev @(Snap $b6 $snapSha) ($tags + @(New-ReleaseTag 'v1.1.0-beta.6' $snapSha $true)) $own $false; $r.Mode -eq 'Tagged' -and $r.Sha -eq $snapSha }
    Test-Case 'its tag made here but not pushed: continue before the push' {
        (Resolve-BetaStart $dev @(Snap $b6 $snapSha) ($tags + @(New-ReleaseTag 'v1.1.0-beta.6' $snapSha $false)) $own $false).Mode -eq 'Resume' }
    Test-Case 'the first beta of a version' {
        (Resolve-BetaStart $dev @(Snap 'release/snapshot/v1.2.0-beta.1' $snapSha) $tags "release/snapshot/v1.2.0-beta.1 $snapSha" $false).Tag -eq 'v1.2.0-beta.1' }
    Test-Throws "another checkout's snapshot: one release at a time" 'another release is going on.*-Continue' { Resolve-BetaStart $dev @(Snap $b6 $snapSha) $tags '' $false }
    Test-Throws 'a marker of an older snapshot of the same name does not count' 'another release is going on' { Resolve-BetaStart $dev @(Snap $b6 $snapSha) $tags "$b6 $other" $false }
    Test-Case '-Continue takes it over' { (Resolve-BetaStart $dev @(Snap $b6 $snapSha) $tags '' $true).Mode -eq 'Resume' }
    Test-Throws '-Continue without a snapshot' 'no snapshot branch' { Resolve-BetaStart $dev @() $tags '' $true }
    Test-Throws 'several snapshots' 'several snapshot branches' { Resolve-BetaStart $dev @((Snap $b6 $snapSha), (Snap 'release/snapshot/v1.1.0-beta.7' $other)) $tags $own $false }
    Test-Throws 'its tag names another commit: stale' 'stale.*git push origin --delete release/snapshot/v1\.1\.0-beta\.6' {
        Resolve-BetaStart $dev @(Snap $b6 $snapSha) ($tags + @(New-ReleaseTag 'v1.1.0-beta.6' $other $true)) $own $false }
    Test-Throws 'a later beta was tagged meanwhile: stale' 'next beta of 1\.1\.0 is beta\.8.*stale' {
        Resolve-BetaStart $dev @(Snap $b6 $snapSha) ($tags + @(New-ReleaseTag 'v1.1.0-beta.7' $other $true)) $own $false }
    Test-Throws 'its version is released: stale' 'v1\.1\.0 is released already' { Resolve-BetaStart $dev @(Snap $b6 $snapSha) ($tags + @(New-ReleaseTag 'v1.1.0' $other $true)) $own $false }
    Test-Throws 'a branch under release/snapshot/ that is no beta' 'not a snapshot of a beta' { Resolve-BetaStart $dev @(Snap 'release/snapshot/test' $snapSha) $tags 'release/snapshot/test aaaa' $false }

    Write-Host 'Whether CI has to run on a snapshot first'
    Test-Case "dev's CI passed on the commit: tag it right away" { $d = Get-CiDecision @(Run 'dev' 'completed' 'success' 7) $b6 $false; $d.Action -eq 'passed' -and $d.Run.databaseId -eq 7 }
    Test-Case "dev's CI still going: push a snapshot (a push to dev would cancel it)" { (Get-CiDecision @(Run 'dev' 'in_progress') $b6 $false).Action -eq 'push' }
    Test-Case "dev's CI cancelled (2026-10-15): push a snapshot" { (Get-CiDecision @(Run 'dev' 'completed' 'cancelled') $b6 $false).Action -eq 'push' }
    Test-Case 'no CI run at all: push a snapshot' { $d = Get-CiDecision @() $b6 $false; $d.Action -eq 'push' -and $null -eq $d.Run }
    Test-Case "dev's CI failed on the commit: refuse" { (Get-CiDecision @(Run 'dev' 'completed' 'failure') $b6 $false).Action -eq 'refuse' }
    Test-Case 'only a tag build passed on the commit: push a snapshot (tag builds never count)' { (Get-CiDecision @(Run 'v1.1.0-beta.5' 'completed' 'success') $b6 $false).Action -eq 'push' }
    Test-Case 'a promotion branch passed on the commit: tag it right away' { (Get-CiDecision @(Run 'release/1.1.0' 'completed' 'success') $b6 $false).Action -eq 'passed' }
    Test-Case 'the snapshot is pushed, its run not started yet: wait for it' { (Get-CiDecision @(Run 'dev' 'in_progress') $b6 $true).Action -eq 'start' }
    Test-Case 'the snapshot run is going: reuse it' { $d = Get-CiDecision @((Run 'dev' 'completed' 'cancelled' 1), (Run $b6 'in_progress' '' 2)) $b6 $true; $d.Action -eq 'wait' -and $d.Run.databaseId -eq 2 }
    Test-Case 'the snapshot run is going, though dev failed on the commit: wait for the snapshot' { (Get-CiDecision @((Run 'dev' 'completed' 'failure' 1), (Run $b6 'queued' '' 2)) $b6 $true).Action -eq 'wait' }
    Test-Case 'the snapshot run passed: tag' { $d = Get-CiDecision @(Run $b6 'completed' 'success' 2) $b6 $true; $d.Action -eq 'passed' -and $d.Run.headBranch -eq $b6 }
    Test-Case 'the snapshot run failed: stop' { $d = Get-CiDecision @(Run $b6 'completed' 'failure' 2) $b6 $true; $d.Action -eq 'failed' -and $d.Run.databaseId -eq 2 }
    Test-Case 'the snapshot run was cancelled: stop' { (Get-CiDecision @(Run $b6 'completed' 'cancelled' 2) $b6 $true).Action -eq 'failed' }
    Test-Case "the snapshot run failed, but dev's CI passed on the same commit since: tag" { (Get-CiDecision @((Run $b6 'completed' 'failure' 2), (Run 'dev' 'completed' 'success' 3)) $b6 $true).Action -eq 'passed' }
    Test-Case "another beta's snapshot run does not count" { (Get-CiDecision @(Run 'release/snapshot/v1.1.0-beta.5' 'in_progress' '' 2) $b6 $true).Action -eq 'start' }

    Write-Host 'Hints after a failed snapshot run'
    $failedHint = Get-SnapshotRunHint (Run $b6 'completed' 'failure' 42) $b6
    $cancelledHint = Get-SnapshotRunHint (Run $b6 'completed' 'cancelled' 43) $b6
    Test-Case 'failed: re-run the failed jobs, then run this again (it reuses the run)' { $failedHint -match 'gh run rerun 42 --failed --repo qiyinxi/HomePodCast\), then run this again: it continues with release/snapshot/v1\.1\.0-beta\.6 and that run' }
    Test-Case 'failed: if the code is broken, delete the snapshot' { $failedHint -match 'code is broken.*git push origin --delete release/snapshot/v1\.1\.0-beta\.6' }
    Test-Case 'cancelled: re-run it, then run this again' { $cancelledHint -match 'gh run rerun 43 --repo qiyinxi/HomePodCast\), then run this again' -and $cancelledHint -notmatch '--failed' }

    Write-Host 'Other releases going on'
    Test-Case 'nothing going on' { @(Find-ReleaseInProgress @() @((Run 'dev' 'in_progress'), (Run 'v1.1.0-beta.5' 'completed' 'success'))).Count -eq 0 }
    Test-Case 'an open PR to main' { @(Find-ReleaseInProgress @([pscustomobject]@{ number = 50; headRefName = 'release/1.1.0'; url = 'u' }) @()).Count -eq 1 }
    Test-Case 'another tag build running' { "$(Find-ReleaseInProgress @() @(Run 'v1.1.0-beta.5' 'in_progress') 'v1.1.0-beta.6')" -match 'tag build of v1\.1\.0-beta\.5' }
    Test-Case 'its own tag build is no other release' { @(Find-ReleaseInProgress @() @(Run 'v1.1.0-beta.6' 'in_progress') 'v1.1.0-beta.6').Count -eq 0 }
    Test-Case "a promotion branch's CI running" { "$(Find-ReleaseInProgress @() @(Run 'release/1.1.0' 'queued'))" -match 'promotion' }
    Test-Case "a snapshot branch's CI is no promotion" { @(Find-ReleaseInProgress @() @(Run $b6 'in_progress')).Count -eq 0 }

    if ($script:failures) { Write-Host "$script:failures of $script:count self-test cases gave the wrong result" -ForegroundColor Red; return $false }
    Write-Host "all $script:count self-test cases ok" -ForegroundColor Green
    $true
}

$ScriptPath = $PSCommandPath
if ($SelfTest) { if (Invoke-SelfTest) { exit 0 } else { exit 1 } }

# The repository of the current directory, and its GitHub name for gh.
$root = & git rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -or -not $root) { Write-Host 'release.ps1: run it inside a clone of HomePodCast' -ForegroundColor Red; exit 1 }
if (-not (Test-Path (Join-Path $root $Csproj))) { Write-Host "release.ps1: $root is not HomePodCast (no $Csproj)" -ForegroundColor Red; exit 1 }

try {
    if ($PSCmdlet.ParameterSetName -ne 'Status') {
        if (-not $NotesFile) { throw 'a release needs -NotesFile with English notes followed by ## 简体中文' }
        $NotesFile = Resolve-NotesFile $NotesFile
    }
    Push-Location $root
    try {
        $url = "$(Invoke-Git remote get-url $Remote)"
        if ($url -notmatch 'github\.com[:/](?<slug>[^/]+/[^/]+?)(\.git)?/?$') { throw "$Remote ($url) is not a GitHub repository" }
        $Repo = $Matches['slug']
        $R = @('--repo', $Repo)
        switch ($PSCmdlet.ParameterSetName) {
            'Status' { Show-Status }
            'Beta' { Invoke-Beta }
            'Promote' { Invoke-Promote $Promote }
        }
        if ($WhatIf) { Write-Step 'WhatIf: nothing was created, merged or pushed' }
    }
    finally {
        Pop-Location
    }
}
catch {
    Write-Host "release.ps1 stopped: $($_.Exception.Message)" -ForegroundColor Red
    Write-Verbose $_.ScriptStackTrace
    exit 1
}
