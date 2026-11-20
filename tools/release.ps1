<#
release.ps1 - cut a release from main: DRY RUN by default, prints each check and what -Push would do, changes nothing.

  pwsh -NoProfile -File tools/release.ps1           dry run: the checks, then what would happen
  pwsh -NoProfile -File tools/release.ps1 -Push     tag, push the tag, wait for the release, verify it

Checks (both modes): the branch is main, the tree is clean (untracked files count), HEAD is origin/main after a fetch,
the version in src/BluettiWatcher/BluettiWatcher.csproj has its "## [X.Y.Z] - YYYY-MM-DD" section in CHANGELOG.md (the
release notes are that section), and the tag vX.Y.Z exists neither here nor on origin.

-Push, only when every check passed: the annotated tag vX.Y.Z on HEAD, pushed to origin (that starts release.yml); the
release.yml run for HEAD watched to its end (gh run watch); the release's zip and its .sha256 downloaded (gh release
download), the hash compared, the zip unpacked into a temp folder and its install.ps1 run as a dry run (it reads this
PC's install and changes nothing). Needs gh, signed in.

Exit: 0 every check passed (and with -Push the release is verified) · 1 a check failed · 2 could not run (no git, no
gh or not signed in, the fetch failed, no version in the csproj).
#>
[CmdletBinding()]
param(
    [switch] $Push,
    [string] $Repo = (Split-Path -Parent $PSScriptRoot),
    [string] $Remote = 'origin',
    [string] $Branch = 'main',
    [int] $FindRunSec = 120
)
$ErrorActionPreference = 'Stop'
$script:failed = 0
$script:pushed = $false

function Write-Check([bool] $ok, [string] $text) {
    if ($ok) { Write-Output "ok    $text" } else { $script:failed++; Write-Output "FAIL  $text" }
}
function Stop-Release([int] $code, [string] $text) { Write-Output "release: $text"; exit $code }

# git with its output as lines and its exit code; never throws on a non-zero exit (that is information here).
function Invoke-Git {
    $PSNativeCommandUseErrorActionPreference = $false
    $out = @(& git -C $Repo @args 2>&1 | ForEach-Object { "$_" })
    @{ Code = $LASTEXITCODE; Out = $out; Text = ($out -join "`n").Trim() }
}
function Invoke-Gh {
    $PSNativeCommandUseErrorActionPreference = $false
    Push-Location -LiteralPath $Repo
    try { $out = @(& gh @args 2>&1 | ForEach-Object { "$_" }) } finally { Pop-Location }
    @{ Code = $LASTEXITCODE; Out = $out; Text = ($out -join "`n").Trim() }
}

# ---- what this run needs before any check
try {
    if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Stop-Release 2 'COULD NOT RUN: git is not on PATH' }
    if ((Invoke-Git rev-parse --is-inside-work-tree).Text -ne 'true') { Stop-Release 2 "COULD NOT RUN: $Repo is not a git checkout" }
    $csproj = Join-Path $Repo 'src\BluettiWatcher\BluettiWatcher.csproj'
    $changelog = Join-Path $Repo 'CHANGELOG.md'
    $m = if (Test-Path -LiteralPath $csproj) { [regex]::Match([IO.File]::ReadAllText($csproj), '<Version>(\d+\.\d+\.\d+)</Version>') }
    if (-not $m -or -not $m.Success) { Stop-Release 2 "COULD NOT RUN: no <Version>X.Y.Z</Version> in $csproj" }
    $version = $m.Groups[1].Value
    $tag = "v$version"
    if ($Push) {
        if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Stop-Release 2 'COULD NOT RUN: gh is not on PATH (-Push watches the release run and downloads the release)' }
        $auth = Invoke-Gh auth status
        if ($auth.Code -ne 0) { Stop-Release 2 "COULD NOT RUN: gh is not signed in ($($auth.Out | Select-Object -First 1))" }
    }
    $fetch = Invoke-Git fetch --quiet --tags $Remote $Branch
    if ($fetch.Code -ne 0) { Stop-Release 2 "COULD NOT RUN: git fetch $Remote $Branch failed: $($fetch.Text)" }
}
catch { Stop-Release 2 "COULD NOT RUN: $($_.Exception.Message)" }

# ---- the checks (both modes)
Write-Output "release: version $version (tag $tag) from $csproj"
$branchNow = (Invoke-Git rev-parse --abbrev-ref HEAD).Text
Write-Check ($branchNow -ceq $Branch) "on the branch $Branch$(if ($branchNow -cne $Branch) { " (HEAD is on $branchNow)" })"
$dirty = (Invoke-Git status --porcelain --untracked-files=all).Out | Where-Object { $_ }
Write-Check (-not $dirty) "the tree is clean$(if ($dirty) { " ($(@($dirty).Count) changed or untracked: $((@($dirty) | Select-Object -First 3) -join '; '))" })"
$head = (Invoke-Git rev-parse HEAD).Text
$upstream = Invoke-Git rev-parse "refs/remotes/$Remote/$Branch"
Write-Check ($upstream.Code -eq 0 -and $upstream.Text -eq $head) "HEAD $($head.Substring(0, [Math]::Min(7, $head.Length))) is $Remote/$Branch$(if ($upstream.Code -ne 0) { ' (no such remote branch)' } elseif ($upstream.Text -ne $head) { " (that is $($upstream.Text.Substring(0, 7)): pull or push first)" })"
$section = if (Test-Path -LiteralPath $changelog) { [regex]::IsMatch([IO.File]::ReadAllText($changelog), "(?m)^## \[$([regex]::Escape($version))\] - \d{4}-\d{2}-\d{2}\s*$") } else { $false }
Write-Check $section "CHANGELOG.md has the section ## [$version] - <date>"
$localTag = (Invoke-Git rev-parse -q --verify "refs/tags/$tag").Code -eq 0
$remoteTags = Invoke-Git ls-remote --tags $Remote "refs/tags/$tag"
if ($remoteTags.Code -ne 0) { Stop-Release 2 "COULD NOT RUN: git ls-remote $Remote failed: $($remoteTags.Text)" }
$remoteTag = [bool]$remoteTags.Text
Write-Check (-not $localTag -and -not $remoteTag) "the tag $tag is new$(if ($localTag -or $remoteTag) { " (it exists $(@($(if ($localTag) { 'here' }), $(if ($remoteTag) { "on $Remote" }) | Where-Object { $_ }) -join ' and '): bump the version)" })"

if (-not $Push) {
    Write-Output "would tag $tag (annotated) on $head and push it to ${Remote}: release.yml builds and publishes the release"
    Write-Output 'would watch the release.yml run for that commit to its end (gh run watch)'
    Write-Output "would download bluetti-watcher-$version-win-x64.zip and its .sha256 (gh release download $tag), compare the hash, unpack it and run its install.ps1 as a dry run"
    Write-Output "dry run: nothing changed.$(if (-not $script:failed) { ' Run with -Push to release.' })"
    exit $(if ($script:failed) { 1 } else { 0 })
}
if ($script:failed) { Stop-Release 1 "$script:failed check(s) failed: nothing was tagged or pushed" }

# ---- -Push: from here a failure is a failed release (1), never "did not run"
$work = Join-Path ([IO.Path]::GetTempPath()) ("bluetti-watcher-release-$version-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    $made = Invoke-Git tag -a $tag -m "Bluetti Watcher $version"
    if ($made.Code -ne 0) { Stop-Release 1 "git tag $tag failed: $($made.Text)" }
    $sent = Invoke-Git push $Remote "refs/tags/$tag"
    if ($sent.Code -ne 0) { Stop-Release 1 "git push $Remote $tag failed: $($sent.Text) (the local tag stays; delete it with git tag -d $tag)" }
    $script:pushed = $true
    Write-Output "do    tagged $tag on $head and pushed it to $Remote"

    # The run appears a few seconds after the push: found by the tagged commit, bounded.
    $deadline = [DateTime]::UtcNow.AddSeconds($FindRunSec)
    $runId = $null
    while (-not $runId) {
        $list = Invoke-Gh run list --workflow release.yml --event push --limit 20 --json 'databaseId,headSha'
        if ($list.Code -eq 0 -and $list.Text) { $runId = @($list.Text | ConvertFrom-Json | Where-Object { $_.headSha -eq $head } | Select-Object -First 1 -ExpandProperty databaseId) | Select-Object -First 1 }
        if ($runId) { break }
        if ([DateTime]::UtcNow -ge $deadline) { Stop-Release 1 "no release.yml run for $head within $FindRunSec s (gh run list: $($list.Text))" }
        Start-Sleep -Seconds 5
    }
    Write-Output "do    watching the release.yml run $runId"
    $watch = Invoke-Gh run watch $runId --exit-status --interval 30
    if ($watch.Code -ne 0) { Stop-Release 1 "the release.yml run $runId failed (gh run view $runId --log-failed): $(@($watch.Out) | Select-Object -Last 3)" }
    Write-Output "ok    the release.yml run $runId passed"

    $null = New-Item -ItemType Directory -Path $work
    $got = Invoke-Gh release download $tag --pattern '*.zip' --pattern '*.zip.sha256' --dir $work
    if ($got.Code -ne 0) { Stop-Release 1 "gh release download $tag failed: $($got.Text)" }
    $zip = @(Get-ChildItem -LiteralPath $work -Filter '*.zip' -File)
    $sum = @(Get-ChildItem -LiteralPath $work -Filter '*.zip.sha256' -File)
    if ($zip.Count -ne 1 -or $sum.Count -ne 1) { Stop-Release 1 "the release $tag must carry one zip and one .sha256; it has $($zip.Count) and $($sum.Count)" }
    $want = ([IO.File]::ReadAllText($sum[0].FullName).Trim() -split '\s+')[0].ToLowerInvariant()
    $have = (Get-FileHash -LiteralPath $zip[0].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($want -ne $have) { Stop-Release 1 "the hash of $($zip[0].Name) is $have, its .sha256 says $want" }
    Write-Output "ok    $($zip[0].Name) matches its .sha256 ($have)"

    $pkg = Join-Path $work 'pkg'
    Expand-Archive -LiteralPath $zip[0].FullName -DestinationPath $pkg
    $installer = Join-Path $pkg 'install.ps1'
    if (-not (Test-Path -LiteralPath $installer)) { Stop-Release 1 "no install.ps1 in $($zip[0].Name)" }
    $PSNativeCommandUseErrorActionPreference = $false
    $dry = @(& pwsh -NoProfile -NonInteractive -File $installer 2>&1 | ForEach-Object { "$_" })
    $dryCode = $LASTEXITCODE
    $dry | ForEach-Object { Write-Output "      install: $_" }
    if ($dryCode -ne 0) { Stop-Release 1 "the release's install.ps1 dry run exited $dryCode" }
    Write-Output "release: $tag is out and verified (gh release view $tag)"
    exit 0
}
catch { Stop-Release $(if ($script:pushed) { 1 } else { 2 }) "$(if ($script:pushed) { "FAILED after the push of ${tag}: " } else { 'COULD NOT RUN: ' })$($_.Exception.Message)" }
finally { if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue } }
