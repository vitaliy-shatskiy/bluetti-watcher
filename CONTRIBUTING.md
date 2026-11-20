# Contributing

## Build and run

Requirements: Windows, the .NET SDK named in `global.json`, PowerShell 7.

```powershell
dotnet build bluetti-watcher.slnx
+
pwsh -NoProfile -File tools/publish.ps1 -Out C:\bw-out                              # NativeAOT; -Fallback without the MSVC linker
C:\bw-out\BluettiWatcher.exe --dry-run --data-dir C:\bw-data --flag $env:TEMP\power-mode-dev.json
```

`--dry-run` logs every power-mode command and hibernation instead of running it. Put a `config.json` with `mac` in the
data directory first ([docs/configuration.md](docs/configuration.md)).

## Tests

```powershell
dotnet test --solution bluetti-watcher.slnx -c Release
pwsh -NoProfile -File tools/test-swap.ps1
pwsh -NoProfile -File tools/test-wake-sddl.ps1
pwsh -NoProfile -File tools/test-install.ps1
```

The pwsh tests exit 0 when every case passed, 1 when one failed, 2 when they could not run. None of the tests touches
the station, the installed service or a scheduled task. CI runs all of them on every push and pull request.

The .NET tests are xUnit.net v3 on Microsoft.Testing.Platform (`global.json` selects it): `dotnet test` takes the
solution through `--solution` (a project through `--project`). To run a subset:

```powershell
dotnet test --solution bluetti-watcher.slnx -c Release --filter "FullyQualifiedName~CloudReaderTests"  # VSTest syntax
dotnet test --solution bluetti-watcher.slnx -c Release --filter-class "*.CloudReaderTests"             # '*' at either end
dotnet test --solution bluetti-watcher.slnx -c Release --filter-method "*.CloudReaderTests.The_allow_list_refuses_any_other_call"
```

`--filter-namespace`, `--filter-trait`, `--filter-display-name`, their `--filter-not-*` forms and `--filter-query` also
work; `dotnet test --project tests/BluettiWatcher.Tests --help` lists them. A filter that matches nothing exits 8.

## Style

- `.editorconfig` holds the formatting and the analyzer settings; the build runs every analyzer and treats warnings as
  errors, so a clean `dotnet build` is the style check.
- Comments state a rule or a reason, not the history of the code.
- The station stays read only: no change may add a write or control path to the station or the cloud.
- Behaviour changes update [docs/design.md](docs/design.md) (and [docs/configuration.md](docs/configuration.md) or
  [docs/api.md](docs/api.md) when a key or a field changes).

## Releasing

A release is the tag `vX.Y.Z` on main; `.github/workflows/release.yml` builds, packages and publishes it. Cut it with:

```powershell
pwsh -NoProfile -File tools/release.ps1          # dry run: the checks, then what -Push would do; changes nothing
pwsh -NoProfile -File tools/release.ps1 -Push    # tag, push the tag, wait for release.yml, verify the release
```

Checks: on main, a clean tree (untracked files count), HEAD equal to origin/main after a fetch, the `<Version>` of
`src/BluettiWatcher/BluettiWatcher.csproj` has its `## [X.Y.Z] - YYYY-MM-DD` section in `CHANGELOG.md` (the release
notes), and no tag `vX.Y.Z` here or on origin. `-Push` then makes the annotated tag, pushes it, watches the release.yml
run (`gh run watch`), downloads the zip and its `.sha256` (`gh release download`), compares the hash, unpacks the zip
and runs its `install.ps1` as a dry run. It needs `gh`, signed in. Exit: 0 passed · 1 a check failed · 2 could not run.
Its offline test (throwaway repositories, a fake `gh`): `pwsh -NoProfile -File tools/test-release.ps1`.

## Pull requests

One change per pull request, with tests for the behaviour it adds or fixes, all tests green, and a description of what
changed and how you checked it. Hardware-dependent changes (Bluetooth, hibernation, wake timers): say which station and
Windows version you tried them on.
