# Contributing

Requirements: Windows, the .NET SDK `global.json` names (11.0.100, patch roll-forward), PowerShell 7. A NativeAOT
publish also needs the MSVC linker (Visual Studio C++ build tools).

## Build and run

```powershell
dotnet build bluetti-watcher.slnx
pwsh -NoProfile -File tools/publish.ps1 -Out C:\bw-out          # NativeAOT; -Fallback = trimmed self-contained single file, no MSVC linker
C:\bw-out\BluettiWatcher.exe --dry-run --data-dir C:\bw-data --port 18941 --flag $env:TEMP\power-mode-dev.json
```

- `publish.ps1` writes `publish.log` beside the output. Exit 0 published, 1 failed (last log lines printed), 2 could
  not run.
- `--dry-run` decides and logs; it never hibernates and logs `dry run, would run: ...` instead of a power-mode command.
  Put a `config.json` with `mac` in the data directory first ([configuration.md](docs/configuration.md)). A separate
  `--port` and `--flag` keep a dev run off an installed service's page and flag.

## Tests

```powershell
dotnet test --solution bluetti-watcher.slnx -c Release
$env:PM_SWITCH_ENTRY = 'script'; dotnet test --solution bluetti-watcher.slnx -c Release --no-build --filter FullyQualifiedName~BluettiWatcher.Tests.PowerModeScript
Get-ChildItem tools -Filter 'test-*.ps1' | ForEach-Object { pwsh -NoProfile -File $_.FullName }
```

| Test | What it covers |
|---|---|
| `dotnet test` | xUnit.net v3 on Microsoft.Testing.Platform. The solution goes through `--solution`, a project through `--project`. |
| `PM_SWITCH_ENTRY=script` | The power-mode tests again, every call through `power-mode/*.ps1` ([power-mode README](power-mode/README.md#tests)). |
| `tools/test-install.ps1` | `install.ps1`'s dry run against a temp data directory. |
| `tools/test-release.ps1` | `tools/release.ps1` against throwaway repositories and a fake `gh`; offline. |
| `tools/test-swap.ps1` | The binary swap rules of `tools/swap.ps1` on temp directories. |
| `tools/test-wake-sddl.ps1` | The wake task's access-list read-back and name rules, against a fake Task Scheduler. |

The pwsh tests exit 0 when every case passed, 1 when one failed, 2 when they could not run. No test touches the
station, the installed service or a real scheduled task. CI (`.github/workflows/ci.yml`) runs all three lines on every
push to main and every pull request, except changes to documentation only.

A subset:

```powershell
dotnet test --solution bluetti-watcher.slnx -c Release --filter "FullyQualifiedName~CloudReaderTests"
dotnet test --solution bluetti-watcher.slnx -c Release --filter-class "*.CloudReaderTests"
```

`dotnet test --project tests/BluettiWatcher.Tests --help` lists the other filters. A filter that matches nothing exits
8.

`tools/bench.ps1` measures the exe's footprint (startup, memory, threads, idle CPU, API latency, response and file
sizes) against its budgets, on a test port with scratch data. Run it on a quiet machine; it is not part of CI.

## Style

- `Directory.Build.props` turns on every analyzer and code style in the build and treats warnings as errors;
  `.editorconfig` holds the settings. A clean `dotnet build` is the style check.
- Comments state a rule or a reason, never the history of the code.
- The station stays read only: no change may add a write or control path to the station or the cloud.
- A behaviour change updates [docs/design.md](docs/design.md); a new key or field updates
  [docs/configuration.md](docs/configuration.md) or [docs/api.md](docs/api.md); a register change updates
  [docs/bluetti-protocol.md](docs/bluetti-protocol.md).
- A change to the quick start or the reaction table updates both `README.md` and `README.uk.md`.
- A user-visible change gets a line under `## [Unreleased]` in `CHANGELOG.md`.
- README images are made by scripts: [docs/media/README.md](docs/media/README.md).

## Pull requests

One change per pull request, with tests for the behaviour it adds or fixes and all tests green. Fill in the template:
what changed, the commands you ran and their result. A hardware-dependent change (Bluetooth, hibernation, wake timers)
names the station and the Windows version it was tried on.

## Releasing

A release is the tag `vX.Y.Z` on main; `.github/workflows/release.yml` builds, packages and publishes it.

```powershell
pwsh -NoProfile -File tools/release.ps1          # dry run: every check, then what -Push would do; changes nothing
pwsh -NoProfile -File tools/release.ps1 -Push    # tag, push the tag, watch release.yml, verify the release
```

Checks: on main, a clean tree (untracked files count), HEAD equal to origin/main after a fetch, a `## [X.Y.Z] -
YYYY-MM-DD` section in `CHANGELOG.md` for the `<Version>` of `src/BluettiWatcher/BluettiWatcher.csproj` (that section
is the release notes), and no tag `vX.Y.Z` here or on origin. `-Push` then pushes an annotated tag, watches the run
(`gh run watch`), downloads the zip and its `.sha256`, compares the hash and runs the zip's `install.ps1` as a dry run.
It needs `gh`, signed in. Exit 0 passed, 1 a check failed, 2 could not run.
