#Requires -Version 7
<#
.SYNOPSIS
  Checks that a pushed tag may be released. build.yml runs this first on every tag build (docs/RELEASING.md).

.DESCRIPTION
  CI publishes every v* tag as a GitHub release, so a tag must name code that has already passed CI, and a stable
  tag must name exactly the code of a beta that was published and tested. For -Tag on commit -Sha:
    1. the tag is vX.Y.Z, vX.Y.Z-beta.N or vX.Y.Z-rc.N, and X.Y.Z is the <Version> in src/HomePodCast.csproj at -Sha;
    2. -Sha already has a successful run of the build workflow from a push to dev, main or release/** (the tag
       build itself, -ExcludeRunId, doesn't count);
    3. stable tags (vX.Y.Z) only: -Sha is on main, and a vX.Y.Z-beta.N (or -rc.N) tag points to a commit with the
       same source tree as -Sha and is published (not a draft) as a GitHub prerelease.
  It reads everything through the GitHub API (gh api), so it needs no history in the checkout. Any failure is
  printed (as an error annotation in GitHub Actions) and the script exits with 1.

  The rules hold for every tag pushed since they were added. Older tags are never rebuilt: re-running one of their
  workflow runs uses the workflow file of their own commit, which has no release checks. (v0.2.1, for one, had no
  beta and would fail check 3.)

  -SelfTest runs the checks for known tags and commits of the real repository (read-only) and compares the results
  with what they must be.

.EXAMPLE
  ./tools/release-checks.ps1 -Tag v0.2.2-beta.4 -Sha 549e83f -Repo qiyinxi/HomePodCast
.EXAMPLE
  ./tools/release-checks.ps1 -SelfTest
#>
[CmdletBinding(DefaultParameterSetName = 'Check')]
param(
    # The pushed tag, e.g. v0.2.2-beta.4 (GITHUB_REF_NAME in CI).
    [Parameter(ParameterSetName = 'Check', Mandatory)] [string] $Tag,
    # The tagged commit (GITHUB_SHA in CI); a short SHA works too.
    [Parameter(ParameterSetName = 'Check', Mandatory)] [string] $Sha,
    # owner/name (GITHUB_REPOSITORY in CI). Default: the repository of the current directory's origin remote.
    [string] $Repo = $env:GITHUB_REPOSITORY,
    # The workflow whose branch runs count.
    [string] $Workflow = 'build.yml',
    # This tag build's own run (GITHUB_RUN_ID in CI), never counted as the branch run.
    [Parameter(ParameterSetName = 'Check')] [long] $ExcludeRunId = 0,
    [Parameter(ParameterSetName = 'SelfTest', Mandatory)] [switch] $SelfTest
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$TagPattern = '^v(?<ver>(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*))(-(?<pre>beta|rc)\.(?<n>[1-9]\d*))?$'
$Csproj = 'src/HomePodCast.csproj'
$InCi = $env:GITHUB_ACTIONS -eq 'true'

# GET from the GitHub API; $null for a 404 when -AllowNotFound, otherwise any failure throws.
function Invoke-Api([string] $path, [switch] $AllowNotFound) {
    $out = & gh api -H 'Accept: application/vnd.github+json' -H 'X-GitHub-Api-Version: 2022-11-28' $path 2>&1
    if ($LASTEXITCODE) {
        $text = ($out | ForEach-Object { "$_" }) -join ' '
        if ($AllowNotFound -and $text -match 'HTTP 404') { return $null }
        throw "gh api $path failed (exit code $LASTEXITCODE): $text"
    }
    ($out | Where-Object { $_ -isnot [System.Management.Automation.ErrorRecord] }) -join "`n" | ConvertFrom-Json
}

function Get-Short([string] $sha) { $sha.Substring(0, [Math]::Min(7, $sha.Length)) }

# Follows an annotated tag (or a chain of them) to its commit.
function Resolve-TagCommit([string] $repo, $object) {
    while ($object.type -eq 'tag') { $object = (Invoke-Api "repos/$repo/git/tags/$($object.sha)").object }
    if ($object.type -ne 'commit') { return $null }
    $object.sha
}

# Runs the checks; returns the log lines and the errors (an empty error list means the tag may be released).
function Test-ReleaseTag([string] $tag, [string] $sha, [string] $repo, [string] $workflow, [long] $excludeRunId) {
    $log = [System.Collections.Generic.List[string]]::new()
    $errors = [System.Collections.Generic.List[string]]::new()
    $result = [pscustomobject]@{ Log = $log; Errors = $errors }

    # 1. Tag name and version
    if ($tag -notmatch $TagPattern) {
        $errors.Add("Tag '$tag' is not vX.Y.Z, vX.Y.Z-beta.N or vX.Y.Z-rc.N (N from 1, no leading zeros). Release tags are made by tools/release.ps1; see docs/RELEASING.md.")
        return $result
    }
    $version = $Matches['ver']
    $stable = -not $Matches['pre']

    $commit = Invoke-Api "repos/$repo/commits/$sha" -AllowNotFound
    if (-not $commit) {
        $errors.Add("Commit $sha is not in $repo.")
        return $result
    }
    $sha = $commit.sha
    $short = Get-Short $sha
    $tree = $commit.commit.tree.sha
    $subject = $commit.commit.message.Split("`n")[0]
    if ($subject.Length -gt 90) { $subject = $subject.Substring(0, 89) + '…' }
    $log.Add("$tag -> commit $short ($subject)")

    $file = Invoke-Api "repos/$repo/contents/$($Csproj)?ref=$sha" -AllowNotFound
    $csprojVersion = $null
    if ($file) {
        $xml = [System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String(($file.content -replace '\s', '')))
        if ($xml -match '<Version>\s*([^<\s]+)\s*</Version>') { $csprojVersion = $Matches[1] }
    }
    if (-not $csprojVersion) {
        $errors.Add("$Csproj at $short has no <Version>.")
    }
    elseif ($csprojVersion -ne $version) {
        $errors.Add("Tag $tag is version $version, but $Csproj at $short says <Version>$csprojVersion</Version>. Change the version on dev in a commit of its own ('Version $version'), push it, wait for its CI to pass, then tag that commit.")
    }
    else {
        $log.Add("version $version = <Version> in $Csproj at $short")
    }

    # 2. A branch run of this workflow already passed on this commit
    $runs = @((Invoke-Api "repos/$repo/actions/workflows/$workflow/runs?head_sha=$sha&event=push&per_page=100").workflow_runs |
        Where-Object { ($_.head_branch -in 'dev', 'main' -or $_.head_branch -like 'release/*') -and $_.id -ne $excludeRunId } |
        Sort-Object id -Descending)
    $passed = @($runs | Where-Object { $_.status -eq 'completed' -and $_.conclusion -eq 'success' })
    if ($passed) {
        $log.Add("CI passed on a branch push: $($passed[0].head_branch), run $($passed[0].id) ($($passed[0].html_url))")
    }
    else {
        $seen = if ($runs) { ' Branch runs of this commit: ' + (($runs | ForEach-Object { "$($_.head_branch) run $($_.id) $(if ($_.status -eq 'completed') { $_.conclusion } else { $_.status })" }) -join ', ') + '.' } else { ' It has no branch run at all.' }
        $errors.Add("Commit $short has no successful run of $workflow from a push to dev, main or release/**.$seen Push the branch, wait for its CI to pass, then tag. If that branch run was still going, re-run this tag build once it has passed; if it failed, fix the code on dev and release the next beta number (never move or delete a pushed tag).")
    }

    # 3. Stable releases: on main, and exactly the source of a published beta
    if ($stable) {
        $compare = Invoke-Api "repos/$repo/compare/$sha...main?per_page=1" -AllowNotFound
        if ($compare -and $compare.status -in 'identical', 'ahead') {
            $log.Add("$short is on main")
        }
        else {
            $errors.Add("Stable tag $tag is on commit $short, which is not on main. A stable release is tagged on the merge commit that tools/release.ps1 -Promote makes on main (release/$version -> main), after main's CI for it has passed.")
        }

        $refs = @(Invoke-Api "repos/$repo/git/matching-refs/tags/v$version-")
        # Newest first: rc before beta, higher numbers first.
        $candidates = @(@(foreach ($ref in $refs) {
                    $name = $ref.ref -replace '^refs/tags/', ''
                    if ($name -match $TagPattern -and $Matches['ver'] -eq $version -and $Matches['pre']) {
                        $rank = if ($Matches['pre'] -eq 'rc') { 1 } else { 0 }
                        [pscustomobject]@{ Name = $name; Rank = $rank; N = [int]$Matches['n']; Object = $ref.object }
                    }
                }) | Sort-Object Rank, N -Descending)
        $checked = [System.Collections.Generic.List[string]]::new()
        $match = $null
        foreach ($candidate in $candidates) {
            $betaSha = Resolve-TagCommit $repo $candidate.Object
            if (-not $betaSha) { continue }
            $betaTree = (Invoke-Api "repos/$repo/git/commits/$betaSha").tree.sha
            $checked.Add("$($candidate.Name) $(Get-Short $betaSha)")
            if ($betaTree -ne $tree) { continue }
            $release = Invoke-Api "repos/$repo/releases/tags/$($candidate.Name)" -AllowNotFound
            if ($release -and -not $release.draft -and $release.prerelease) {
                $match = $candidate.Name
                $log.Add("same source tree as $($candidate.Name) (commit $(Get-Short $betaSha), tree $(Get-Short $tree)), published as a prerelease: $($release.html_url)")
                break
            }
            $state = if (-not $release) { 'has no published GitHub release (its tag build failed, or the release is a draft or was deleted)' } else { 'is published, but not as a prerelease' }
            $log.Add("$($candidate.Name) has the same source tree but $state")
        }
        if (-not $match) {
            $list = if ($checked.Count) { " Checked: $($checked -join ', ')." } else { " There is no v$version-beta.N tag." }
            $errors.Add("Stable tag ${tag}: no published v$version-beta.N prerelease has the same source tree as $short (tree $(Get-Short $tree)).$list A stable release is a promotion of a tested beta: release this source as a beta first (tools/release.ps1 -Beta), test it, then promote it (tools/release.ps1 -Promote v$version-beta.N).")
        }
    }
    $result
}

function Resolve-Repo {
    if ($Repo) { return $Repo }
    $url = & git remote get-url origin
    if ($LASTEXITCODE -or "$url" -notmatch 'github\.com[:/](?<slug>[^/]+/[^/]+?)(\.git)?/?$') { throw 'pass -Repo owner/name (no GitHub origin remote here)' }
    $Matches['slug']
}

function Write-Annotation([string] $message) {
    if ($InCi) {
        $escaped = $message -replace '%', '%25' -replace "`r", '%0D' -replace "`n", '%0A'
        Write-Host "::error title=Release checks::$escaped"
    }
    else {
        Write-Host "FAIL: $message" -ForegroundColor Red
    }
}

$repo = Resolve-Repo

if ($SelfTest) {
    # Facts of the real repository (2026-10-11). Expect: 'pass', or the patterns that the errors must match, one per
    # error, in order.
    $cases = @(
        @{ Tag = 'v0.2.2-beta.4'; Sha = '549e83f'; Expect = 'pass'; Why = 'dev CI passed before the tag build' }
        @{ Tag = 'v0.2.2-beta.3'; Sha = 'a4ead8a'; Expect = @('no successful run.*dev run \d+ failure'); Why = "tagged while dev's CI was running; that run failed" }
        @{ Tag = 'v0.2.2-beta.2'; Sha = '2b83fed'; Expect = @('no successful run.*dev run \d+ failure'); Why = "tagged while dev's CI was running; that run failed" }
        @{ Tag = 'v0.2.2-beta.1'; Sha = 'd28e683'; Expect = 'pass'; Why = 'dev CI passed' }
        @{ Tag = 'v0.2.2-beta.4'; Sha = '549e83f'; ExcludeRunId = 38156531915; Expect = @('no successful run.*It has no branch run at all'); Why = 'its only branch run is excluded (as if it were this build)' }
        @{ Tag = 'v0.2.3-beta.1'; Sha = '549e83f'; Expect = @('is version 0\.2\.3, but src/HomePodCast\.csproj .*0\.2\.2'); Why = 'tag version differs from the csproj' }
        @{ Tag = 'v0.2.2'; Sha = '549e83f'; Expect = @('which is not on main'); Why = 'same source as v0.2.2-beta.4, but not merged into main' }
        @{ Tag = 'v0.2.1'; Sha = '4aae077'; Expect = @('no published v0\.2\.1-beta\.N.*There is no v0\.2\.1-beta\.N tag'); Why = 'released before the rule: 0.2.1 had no beta' }
        @{ Tag = 'v0.2.0'; Sha = '58a77aa'; Expect = @('no published v0\.2\.0-beta\.N.*Checked: v0\.2\.0-beta\.8'); Why = 'released before the rule: changes after beta.8' }
        @{ Tag = 'v0.2.2-beta'; Sha = '549e83f'; Expect = @('is not vX\.Y\.Z'); Why = 'no beta number' }
        @{ Tag = 'v0.2.2-beta.04'; Sha = '549e83f'; Expect = @('is not vX\.Y\.Z'); Why = 'leading zero' }
        @{ Tag = 'v0.2.2-alpha.1'; Sha = '549e83f'; Expect = @('is not vX\.Y\.Z'); Why = 'only beta and rc prereleases' }
        @{ Tag = '0.2.2'; Sha = '549e83f'; Expect = @('is not vX\.Y\.Z'); Why = 'no v' }
    )
    $failed = 0
    foreach ($case in $cases) {
        $exclude = if ($case.ContainsKey('ExcludeRunId')) { $case.ExcludeRunId } else { 0 }
        $r = Test-ReleaseTag $case.Tag $case.Sha $repo $Workflow $exclude
        $errors = @($r.Errors)
        $patterns = @($case.Expect | Where-Object { $_ -ne 'pass' })
        $ok = $errors.Count -eq $patterns.Count
        for ($i = 0; $ok -and $i -lt $errors.Count; $i++) { $ok = $errors[$i] -match $patterns[$i] }
        $expected = if ($patterns.Count) { 'fail' } else { 'pass' }
        $label = "{0,-15} @ {1}  expect {2}: {3}" -f $case.Tag, $case.Sha, $expected, $case.Why
        if ($ok) { Write-Host "ok    $label" -ForegroundColor Green } else { $failed++; Write-Host "WRONG $label" -ForegroundColor Red }
        foreach ($line in $r.Log) { Write-Host "        $line" }
        foreach ($e in $errors) { Write-Host "        error: $e" -ForegroundColor DarkYellow }
    }
    if ($failed) { Write-Host "$failed of $($cases.Count) self-test cases gave the wrong result" -ForegroundColor Red; exit 1 }
    Write-Host "all $($cases.Count) self-test cases ok" -ForegroundColor Green
    exit 0
}

Write-Host "Release checks for $Tag at $Sha in $repo"
$r = Test-ReleaseTag $Tag $Sha $repo $Workflow $ExcludeRunId
foreach ($line in $r.Log) { Write-Host "  ok: $line" }
if ($r.Errors.Count) {
    foreach ($e in $r.Errors) { Write-Annotation $e }
    Write-Host "$Tag may not be released (docs/RELEASING.md). The tag stays; see 'When a tag build fails' there."
    exit 1
}
Write-Host "$Tag may be released"
