#Requires -Version 7
<#
.SYNOPSIS
  Makes HomePodCast releases: betas from dev, and stable releases by promoting a tested beta (docs/RELEASING.md).

.DESCRIPTION
  -Status
      The version on dev, the latest tags, whether dev's CI is green and which betas can be promoted. Read-only.

  -Beta [-NotesFile <md>] [-WhatIf]
      On dev, with a clean working tree (an untracked icon/ aside) and HEAD pushed to origin/dev: waits for dev's CI
      on HEAD (refuses when it failed or doesn't exist), tags HEAD as the next vX.Y.Z-beta.N (X.Y.Z is <Version> in
      src/HomePodCast.csproj; N follows the highest vX.Y.Z-beta.N tag here or on origin, also one whose build failed),
      pushes the tag, waits for the tag build, which publishes the GitHub prerelease, checks the prerelease and its
      .msi and .zip, and applies -NotesFile.

  -Promote vX.Y.Z-beta.N [-NotesFile <md>] [-WhatIf]
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

  -WhatIf runs every check and prints each step it would take; it creates, merges and pushes nothing.

  Run it inside the repository (any worktree; the current directory's repository is used) with git and an
  authenticated gh. It never force-pushes and never deletes or moves a tag or a branch. CI enforces the same rules
  for every pushed tag: tools/release-checks.ps1.

.EXAMPLE
  ./tools/release.ps1 -Status
.EXAMPLE
  ./tools/release.ps1 -Beta -NotesFile ..\notes\0.2.2-beta.5.md -WhatIf
.EXAMPLE
  ./tools/release.ps1 -Promote v0.2.2-beta.5 -NotesFile ..\notes\0.2.2.md
#>
[CmdletBinding(DefaultParameterSetName = 'Status')]
param(
    [Parameter(ParameterSetName = 'Status')] [switch] $Status,
    [Parameter(ParameterSetName = 'Beta', Mandatory)] [switch] $Beta,
    # The beta to promote, e.g. v0.2.2-beta.4.
    [Parameter(ParameterSetName = 'Promote', Mandatory)] [string] $Promote,
    # Release notes for the GitHub release (Markdown, English first, then 简体中文), applied after the tag build.
    [Parameter(ParameterSetName = 'Beta')] [Parameter(ParameterSetName = 'Promote')] [string] $NotesFile,
    # Run the checks and print the steps without creating, merging or pushing anything.
    [Parameter(ParameterSetName = 'Beta')] [Parameter(ParameterSetName = 'Promote')] [switch] $WhatIf
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Remote = 'origin'
$Workflow = 'build.yml'
$Csproj = 'src/HomePodCast.csproj'
$Checklist = 'docs/device-test-checklist.zh-CN.md'
$TagPattern = '^v(?<ver>(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*))(-(?<pre>beta|rc)\.(?<n>[1-9]\d*))?$'
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
        $pre = "$($Matches['pre'])"
        $sha = if ($remoteTags.ContainsKey($name)) { $remoteTags[$name] } else { $localTags[$name] }
        if ($remoteTags.ContainsKey($name) -and $localTags.ContainsKey($name) -and $localTags[$name] -ne $sha) {
            Write-Caution "the local tag $name names $(Get-Short $localTags[$name]), $Remote's names $(Get-Short $sha); $Remote's counts (pushed tags are never moved)"
        }
        [pscustomobject]@{
            Name = $name
            Sha = $sha
            Version = $Matches['ver']
            SortVersion = [version]$Matches['ver']
            Pre = $pre
            N = if ($pre) { [int]$Matches['n'] } else { 0 }
            Stable = -not $pre
            Rank = if (-not $pre) { 2 } elseif ($pre -eq 'rc') { 1 } else { 0 } # beta < rc < stable
            OnRemote = $remoteTags.ContainsKey($name)
        }
    }
    @($tags | Sort-Object SortVersion, Rank, N)
}

# Makes sure the commit is here (git show, merge-base and rev-parse need it).
function Assert-Commit($tag) {
    & git cat-file -e "$($tag.Sha)^{commit}" 2>$null
    if ($LASTEXITCODE) { Invoke-Git fetch --quiet $Remote "refs/tags/$($tag.Name)" }
}

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

# Waits until the run has finished and returns it; throws, with $hint, unless it passed.
function Wait-RunSuccess($run, [string] $what, [string] $hint) {
    if ($run.status -ne 'completed') {
        Write-Info "waiting for $what (run $($run.databaseId)): $($run.url)"
        & gh run watch $run.databaseId @R --exit-status --compact --interval 30 | Out-Host
        if ($LASTEXITCODE) { Write-Info "gh run watch exited with $LASTEXITCODE" } # the run's conclusion below says why
        $run = Invoke-GhJson run view $run.databaseId @R --json $RunFields
    }
    if ($run.status -ne 'completed') { throw "$what is still $($run.status): $($run.url)" }
    if ($run.conclusion -ne 'success') { throw "$what ended with $($run.conclusion): $($run.url). $hint" }
    Write-Info "$what passed: $($run.url)"
    $run
}

# The highest N among the vX.Y.Z-beta.N tags given; 0 when there are none (a version's first beta: Measure-Object
# returns nothing for no input, and StrictMode refuses .Maximum on it).
function Get-HighestBeta([object[]] $betas) {
    if (-not $betas -or $betas.Count -eq 0) { return 0 }
    [int]($betas | Measure-Object N -Maximum).Maximum
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

# The notes file: there, not empty, and English first (a warning only).
function Resolve-NotesFile([string] $path) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "notes file '$path' not found" }
    $full = (Resolve-Path -LiteralPath $path).Path
    $text = Get-Content -LiteralPath $full -Raw -Encoding utf8
    if (-not $text -or -not $text.Trim()) { throw "notes file '$full' is empty" }
    $cjk = [regex]::Match($text, '\p{IsCJKUnifiedIdeographs}')
    if (-not $cjk.Success) { Write-Caution "$full has no Chinese: the notes are English first, then 简体中文" }
    elseif ($text.Substring(0, $cjk.Index) -notmatch '[A-Za-z]{3,}\W+[A-Za-z]{3,}') { Write-Caution "$full seems to start in Chinese: the notes are English first, then 简体中文" }
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
    if (@($tags | Where-Object { $_.Stable -and $_.Version -eq $version })) {
        Write-Info "v$version is released: change <Version> in $Csproj on dev (a commit 'Version X.Y.Z') before the next beta"
    }
    elseif ($devTags) { Write-Info "dev's HEAD is already $($devTags -join ', '): the next beta ($next) needs new commits on dev" }
    elseif ($devRun -and $devRun.status -eq 'completed' -and $devRun.conclusion -ne 'success') { Write-Info "dev's CI failed on its HEAD: fix it before the next beta ($next)" }
    else { Write-Info "tools/release.ps1 -Beta tags dev's HEAD as $next$(if (-not $devRun -or $devRun.status -ne 'completed') { " once dev's CI has passed" })" }
    foreach ($b in $promotable) {
        Write-Info "tools/release.ps1 -Promote $($b.Name) makes v$($b.Version), once $Checklist is done with it on the real HomePod"
    }
}

function Invoke-Beta {
    Write-Step 'The checkout'
    $branch = "$(Invoke-Git branch --show-current)"
    if ($branch -ne 'dev') { throw "betas are tagged on dev, but this checkout is on '$branch' (git switch dev)" }
    $dirty = Get-DirtyFile
    if ($dirty) { throw "the working tree isn't clean (an untracked icon/ aside); commit or stash first:`n    $($dirty -join "`n    ")" }
    Invoke-Git fetch --quiet $Remote
    $head = Get-RevParse HEAD
    if ($head -ne (Get-RevParse "$Remote/dev")) {
        $ahead, $behind = "$(Invoke-Git rev-list --left-right --count "HEAD...$Remote/dev")" -split '\s+'
        throw "dev is $ahead commit(s) ahead of $Remote/dev and $behind behind it. A beta is tagged on pushed code whose CI has passed: push (git push $Remote dev) or pull first, then run this again"
    }
    Write-Info "dev at $(Get-Short $head) = $Remote/dev: $(Get-Subject $head)"
    $version = Get-CsprojVersion $head
    if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "<Version> in $Csproj is '$version', not X.Y.Z" }
    Write-Info "version $version (<Version> in $Csproj)"

    $tags = Get-ReleaseTag
    if (@($tags | Where-Object Name -eq "v$version")) { throw "v$version is already released. Change <Version> in $Csproj on dev first (a commit of its own, 'Version X.Y.Z'), push it, then run this again" }
    $latestStable = @($tags | Where-Object Stable) | Select-Object -Last 1
    if ($latestStable -and [version]$version -lt $latestStable.SortVersion) { throw "version $version is lower than the latest release $($latestStable.Name)" }
    $onHead = @($tags | Where-Object Sha -eq $head | ForEach-Object Name)
    if ($onHead) { throw "HEAD is already tagged $($onHead -join ', '). Tags are never moved or doubled: if a tag build failed on flaky infrastructure, re-run its failed jobs (gh run rerun <run id> --failed); a new beta needs new commits on dev" }
    $betas = @($tags | Where-Object { $_.Version -eq $version -and $_.Pre -eq 'beta' })
    $n = 1 + (Get-HighestBeta $betas)
    $tag = "v$version-beta.$n"
    Write-Info "next beta: $tag$(if ($betas) { " (after $(($betas | ForEach-Object Name) -join ', '))" })"
    $main = Get-RevParse "$Remote/main"
    if (-not (Test-Ancestor $main $head)) {
        Write-Caution "main ($(Get-Short $main)) is not an ancestor of dev, so $tag couldn't be promoted to v${version}: if it is meant to be, merge main into dev first (git merge $Remote/main)"
    }
    if ($NotesFile) { Write-Info "notes: $NotesFile" }

    Write-Step "dev's CI on $(Get-Short $head)"
    $run = Wait-PushRun $head 'dev' "the push to dev" 1
    if ($WhatIf -and $run.status -ne 'completed') { Write-Plan "wait for dev's CI (run $($run.databaseId), $($run.status)): $($run.url)" }
    else { [void](Wait-RunSuccess $run "dev's CI" "Fix it on dev and push (or, if it was flaky infrastructure, re-run the failed jobs: gh run rerun $($run.databaseId) --failed), then run this again.") }

    Write-Step "Tag $tag"
    Invoke-Change "create the annotated tag $tag on $(Get-Short $head) ('HomePodCast $version beta $n')" { Invoke-Git tag -a $tag -m "HomePodCast $version beta $n" $head }
    Invoke-Change "push $tag to $Remote" { Invoke-Git push $Remote "refs/tags/$tag" | Out-Host }
    Wait-TagBuild $tag $head
    Complete-Release $tag $true
}

function Wait-TagBuild([string] $tag, [string] $sha) {
    Write-Step "The tag build of $tag"
    if ($WhatIf) { Write-Plan "wait for the build of $tag (release checks, build, then the GitHub release)"; return }
    $run = Wait-PushRun $sha $tag "the push of $tag"
    [void](Wait-RunSuccess $run "the build of $tag" ("The tag stays: a pushed tag is never moved or deleted. If it failed on flaky infrastructure, re-run the failed jobs " +
        "(gh run rerun $($run.databaseId) --failed --repo $Repo) and finish by hand (docs/RELEASING.md); if the code is broken, fix it on dev and make the next beta."))
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
    $cleanDev = $branch -eq 'dev' -and -not (Get-DirtyFile) -and (Get-RevParse HEAD) -eq (Get-RevParse "$Remote/dev")
    if ($WhatIf) {
        if ($cleanDev) { Write-Plan "merge $Remote/main into dev ('$subject'), check that no file changed and push dev" }
        else { Write-Plan "print the commands to merge main into dev, since this checkout is not a clean dev at $Remote/dev: $manual" }
        return
    }
    Invoke-Git fetch --quiet $Remote
    if (Test-Ancestor "$Remote/main" "$Remote/dev") { Write-Info 'dev already contains main'; return }
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

# The repository of the current directory, and its GitHub name for gh.
$root = & git rev-parse --show-toplevel 2>$null
if ($LASTEXITCODE -or -not $root) { Write-Host 'release.ps1: run it inside a clone of HomePodCast' -ForegroundColor Red; exit 1 }
if (-not (Test-Path (Join-Path $root $Csproj))) { Write-Host "release.ps1: $root is not HomePodCast (no $Csproj)" -ForegroundColor Red; exit 1 }

try {
    if ($NotesFile) { $NotesFile = Resolve-NotesFile $NotesFile }
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
