<#
test-release.ps1 - tools/release.ps1 against throwaway git repositories in a temp folder (a bare one stands in for
origin) and a fake gh first on PATH: the checks (dirty tree, wrong branch, behind origin, no CHANGELOG section, an
existing tag) and -Push end to end (tag pushed, run watched, zip hash compared, install dry run). Offline; touches
nothing outside its own temp directory, never the real remote or GitHub.

  pwsh -NoProfile -File tools/test-release.ps1

Exit: 0 every case passed · 1 a case failed · 2 did not run.
#>
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$release = Join-Path $PSScriptRoot 'release.ps1'
if (-not (Test-Path -LiteralPath $release)) { Write-Output "test-release: COULD NOT RUN: no $release"; exit 2 }
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Write-Output 'test-release: COULD NOT RUN: git is not on PATH'; exit 2 }
try {
    $base = Join-Path ([IO.Path]::GetTempPath()) ('bluetti-watcher-release-test-' + [Guid]::NewGuid().ToString('N'))
    $null = New-Item -ItemType Directory -Path $base
    # The fake gh: verbs answered from BW_FAKE_GH_* variables the case sets.
    $fakeBin = Join-Path $base 'fake-bin'
    $null = New-Item -ItemType Directory -Path $fakeBin
    [IO.File]::WriteAllText((Join-Path $fakeBin 'gh.cmd'), "@pwsh -NoProfile -NonInteractive -File `"%~dp0gh.ps1`" %*`r`n")
    [IO.File]::WriteAllText((Join-Path $fakeBin 'gh.ps1'), @'
$verb = "$($args[0]) $($args[1])"
switch ($verb) {
    'auth status' { if ($env:BW_FAKE_GH_AUTH -eq 'no') { 'You are not logged into any GitHub hosts.'; exit 1 }; 'Logged in'; exit 0 }
    'run list' { "[{`"databaseId`":42,`"headSha`":`"$env:BW_FAKE_GH_SHA`"}]"; exit 0 }
    'run watch' { "run $($args[2]) watched"; exit [int]$env:BW_FAKE_GH_WATCH }
    'release download' {
        $dir = $args[[array]::IndexOf($args, '--dir') + 1]
        Get-ChildItem -LiteralPath $env:BW_FAKE_GH_ASSETS -File | Copy-Item -Destination $dir
        exit 0
    }
}
"fake gh: unknown call $args"; exit 3
'@)
}
catch { Write-Output "test-release: COULD NOT RUN: $($_.Exception.Message)"; exit 2 }

$script:failed = 0
$script:passed = 0
$script:n = 0
function Check([string] $name, [bool] $ok, [string] $detail = '') {
    if ($ok) { $script:passed++ } else { $script:failed++; Write-Output "FAIL  $name $detail" }
}
# The folder is $args[0], not a named parameter: a git flag like -d would bind to it by prefix.
function Invoke-TestGit { $at, $rest = $args; $said = & git -C $at -c user.name=test -c user.email=test@example.invalid @rest 2>&1; if ($LASTEXITCODE) { throw "git $rest failed in ${at}: $($said -join ' ')" } }

# A bare origin and a clone on main with version 9.8.7, its CHANGELOG section (unless -NoSection), pushed.
function New-World([switch] $NoSection, [string] $Version = '9.8.7') {
    $script:n++
    $root = Join-Path $base "case$script:n"
    $origin = Join-Path $root 'origin.git'
    $clone = Join-Path $root 'clone'
    $null = New-Item -ItemType Directory -Path $origin, (Join-Path $clone 'src\BluettiWatcher')
    Invoke-TestGit $origin init --bare --quiet --initial-branch=main
    Invoke-TestGit $clone init --quiet --initial-branch=main
    [IO.File]::WriteAllText((Join-Path $clone 'src\BluettiWatcher\BluettiWatcher.csproj'), "<Project><PropertyGroup><Version>$Version</Version></PropertyGroup></Project>`n")
    $section = if ($NoSection) { '## [9.8.6] - 2026-01-01' } else { "## [$Version] - 2026-02-03" }
    [IO.File]::WriteAllText((Join-Path $clone 'CHANGELOG.md'), "# Changelog`n`n## [Unreleased]`n`n$section`n`n- a change`n")
    Invoke-TestGit $clone add -A
    Invoke-TestGit $clone commit --quiet -m 'first'
    Invoke-TestGit $clone remote add origin $origin
    Invoke-TestGit $clone push --quiet origin main
    @{ Root = $root; Origin = $origin; Clone = $clone; Head = (& git -C $clone rev-parse HEAD).Trim() }
}
# The release zip the fake gh serves: an install.ps1 that exits $InstallExit; -BadHash writes a wrong .sha256.
function New-Assets($w, [int] $InstallExit = 0, [switch] $BadHash) {
    $pkg = Join-Path $w.Root 'pkg'
    $assets = Join-Path $w.Root 'assets'
    $null = New-Item -ItemType Directory -Path $pkg, $assets
    [IO.File]::WriteAllText((Join-Path $pkg 'install.ps1'), "Write-Output 'dry run: nothing changed.'; exit $InstallExit`n")
    $zip = Join-Path $assets 'bluetti-watcher-9.8.7-win-x64.zip'
    Compress-Archive -Path "$pkg\*" -DestinationPath $zip
    $hash = if ($BadHash) { '0' * 64 } else { (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant() }
    [IO.File]::WriteAllText("$zip.sha256", "$hash  bluetti-watcher-9.8.7-win-x64.zip")
    $assets
}
function Invoke-Release($w, [string[]] $Extra = @(), [hashtable] $Env = @{}) {
    # The tagger: a CI runner has no git identity of its own.
    $vars = @{ BW_FAKE_GH_AUTH = 'yes'; BW_FAKE_GH_SHA = $w.Head; BW_FAKE_GH_WATCH = '0'; BW_FAKE_GH_ASSETS = ''; GIT_COMMITTER_NAME = 'test'; GIT_COMMITTER_EMAIL = 'test@example.invalid' }
    foreach ($k in $Env.Keys) { $vars[$k] = $Env[$k] }
    $saved = @{}
    $savedPath = $env:PATH
    try {
        foreach ($k in $vars.Keys) { $saved[$k] = [Environment]::GetEnvironmentVariable($k); [Environment]::SetEnvironmentVariable($k, $vars[$k]) }
        $env:PATH = "$fakeBin;$env:PATH"
        $out = @(& pwsh -NoProfile -NonInteractive -File $release -Repo $w.Clone @Extra 2>&1 | ForEach-Object { "$_" })
        $code = $LASTEXITCODE
    }
    finally {
        $env:PATH = $savedPath
        # $null passed to a .NET string turns into '': a variable that was not set is removed, never set empty.
        foreach ($k in $saved.Keys) { if ($null -eq $saved[$k]) { Remove-Item -LiteralPath "env:$k" -ErrorAction SilentlyContinue } else { [Environment]::SetEnvironmentVariable($k, $saved[$k]) } }
    }
    @{ Code = $code; Text = ($out -join "`n") }
}
function Test-Tag($dir, [string] $tag = 'v9.8.7') { $null = & git -C $dir rev-parse -q --verify "refs/tags/$tag" 2>&1; $LASTEXITCODE -eq 0 }

try {
    # ---- dry run
    $w = New-World
    $r = Invoke-Release $w
    Check 'dry run, all checks pass: exit 0, says what -Push would do' ($r.Code -eq 0 -and $r.Text -match 'would tag v9\.8\.7' -and $r.Text -notmatch 'FAIL') $r.Text
    Check 'dry run: no tag here or on origin' (-not (Test-Tag $w.Clone) -and -not (Test-Tag $w.Origin))

    $w = New-World
    [IO.File]::WriteAllText((Join-Path $w.Clone 'stray.txt'), 'x')
    $r = Invoke-Release $w
    Check 'an untracked file: exit 1, the tree check fails' ($r.Code -eq 1 -and $r.Text -match 'FAIL  the tree is clean \(1 changed or untracked') $r.Text

    $w = New-World
    [IO.File]::AppendAllText((Join-Path $w.Clone 'CHANGELOG.md'), "edit`n")
    $r = Invoke-Release $w
    Check 'a modified file: exit 1' ($r.Code -eq 1 -and $r.Text -match 'FAIL  the tree is clean') $r.Text

    $w = New-World -NoSection
    $r = Invoke-Release $w
    Check 'no CHANGELOG section for the csproj version: exit 1' ($r.Code -eq 1 -and $r.Text -match 'FAIL  CHANGELOG\.md has the section ## \[9\.8\.7\]') $r.Text

    $w = New-World
    Invoke-TestGit $w.Clone tag -a v9.8.7 -m old
    $r = Invoke-Release $w
    Check 'a local tag of that version: exit 1' ($r.Code -eq 1 -and $r.Text -match 'FAIL  the tag v9\.8\.7 is new \(it exists here:') $r.Text

    $w = New-World
    Invoke-TestGit $w.Clone tag -a v9.8.7 -m old
    Invoke-TestGit $w.Clone push --quiet origin v9.8.7
    Invoke-TestGit $w.Clone tag -d v9.8.7
    $r = Invoke-Release $w
    # The fetch brings the tag here too: either way it is named and the run fails.
    Check 'the tag on origin only: exit 1' ($r.Code -eq 1 -and $r.Text -match 'it exists (here and )?on origin') $r.Text

    $w = New-World
    $other = Join-Path $w.Root 'other'
    Invoke-TestGit $w.Root clone --quiet $w.Origin $other
    [IO.File]::WriteAllText((Join-Path $other 'more.txt'), 'x')
    Invoke-TestGit $other add -A
    Invoke-TestGit $other commit --quiet -m 'more'
    Invoke-TestGit $other push --quiet origin main
    $r = Invoke-Release $w
    Check 'origin/main moved on: exit 1, HEAD is not origin/main' ($r.Code -eq 1 -and $r.Text -match 'FAIL  HEAD \w+ is origin/main \(that is') $r.Text

    $w = New-World
    Invoke-TestGit $w.Clone switch --quiet -c topic
    $r = Invoke-Release $w
    Check 'not on main: exit 1' ($r.Code -eq 1 -and $r.Text -match 'FAIL  on the branch main \(HEAD is on topic\)') $r.Text

    # ---- could not run
    $w = New-World
    [IO.File]::WriteAllText((Join-Path $w.Clone 'src\BluettiWatcher\BluettiWatcher.csproj'), "<Project />`n")
    Invoke-TestGit $w.Clone commit --quiet -am 'no version'
    $r = Invoke-Release $w
    Check 'no version in the csproj: exit 2' ($r.Code -eq 2 -and $r.Text -match 'COULD NOT RUN: no <Version>') $r.Text

    $notRepo = Join-Path $base 'not-a-repo'
    $null = New-Item -ItemType Directory -Path $notRepo
    $r = Invoke-Release @{ Clone = $notRepo; Head = '' }
    Check 'not a git checkout: exit 2' ($r.Code -eq 2 -and $r.Text -match 'COULD NOT RUN: .* is not a git checkout') $r.Text

    $w = New-World
    $r = Invoke-Release $w -Extra @('-Push') -Env @{ BW_FAKE_GH_AUTH = 'no' }
    Check '-Push, gh not signed in: exit 2, no tag' ($r.Code -eq 2 -and $r.Text -match 'gh is not signed in' -and -not (Test-Tag $w.Clone)) $r.Text

    # ---- -Push
    $w = New-World
    [IO.File]::WriteAllText((Join-Path $w.Clone 'stray.txt'), 'x')
    $r = Invoke-Release $w -Extra @('-Push')
    Check '-Push with a failed check: exit 1, nothing tagged or pushed' ($r.Code -eq 1 -and $r.Text -match 'nothing was tagged or pushed' -and -not (Test-Tag $w.Clone) -and -not (Test-Tag $w.Origin)) $r.Text

    $w = New-World
    $r = Invoke-Release $w -Extra @('-Push') -Env @{ BW_FAKE_GH_ASSETS = (New-Assets $w) }
    Check '-Push: exit 0, the annotated tag on origin at HEAD, hash and install dry run verified' ($r.Code -eq 0 -and (Test-Tag $w.Origin) -and (& git -C $w.Origin cat-file -t v9.8.7) -eq 'tag' -and (& git -C $w.Origin rev-parse 'v9.8.7^{commit}') -eq $w.Head -and $r.Text -match 'matches its \.sha256' -and $r.Text -match 'install: dry run: nothing changed' -and $r.Text -match 'is out and verified') $r.Text

    $w = New-World
    $r = Invoke-Release $w -Extra @('-Push') -Env @{ BW_FAKE_GH_ASSETS = (New-Assets $w -BadHash) }
    Check '-Push, the .sha256 does not match the zip: exit 1' ($r.Code -eq 1 -and $r.Text -match 'its \.sha256 says 0{64}' -and $r.Text -notmatch 'is out and verified') $r.Text

    $w = New-World
    $r = Invoke-Release $w -Extra @('-Push') -Env @{ BW_FAKE_GH_ASSETS = (New-Assets $w); BW_FAKE_GH_WATCH = '1' }
    Check '-Push, the release run failed: exit 1, nothing downloaded' ($r.Code -eq 1 -and $r.Text -match 'the release\.yml run 42 failed' -and $r.Text -notmatch 'sha256') $r.Text

    $w = New-World
    $r = Invoke-Release $w -Extra @('-Push') -Env @{ BW_FAKE_GH_ASSETS = (New-Assets $w -InstallExit 1) }
    Check '-Push, the install dry run of the zip fails: exit 1' ($r.Code -eq 1 -and $r.Text -match 'install\.ps1 dry run exited 1') $r.Text
}
catch {
    Write-Output "FAIL  unexpected: $($_.Exception.Message) at $($_.InvocationInfo.ScriptLineNumber)"
    $script:failed++
}
finally {
    Remove-Item -LiteralPath $base -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Output "test-release: $script:passed passed, $script:failed failed"
if ($script:failed -gt 0) { exit 1 }
if ($script:passed -eq 0) { exit 2 }
exit 0
