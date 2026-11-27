# Contributing to FlowLedger

Thank you for considering contributing.

## Development setup

1. Install [.NET 10 SDK](https://dot.net/download).
2. Install Docker Desktop (for Aspire-managed Postgres and Redis).
3. No Aspire workload is needed: Aspire is consumed as NuGet packages and CI builds the whole solution without it.
4. Clone the repo and run `./eng/scripts/run.ps1` (Windows) or `./eng/scripts/run.sh` (Linux/macOS).
5. Install pre-commit hooks (required — prevents secret leaks before commits reach the remote):
   ```bash
   pip install pre-commit
   pre-commit install
   ```
   Hooks: gitleaks, private-key and large-file detection, YAML/JSON/merge-conflict checks and hadolint
   (needs Docker). `dotnet format` is a manual-stage hook: `pre-commit run --hook-stage manual dotnet-format`.
   Gitleaks is the required secret scanner; GitGuardian (`ggshield`) is optional and its CI workflow
   skips itself unless the `GITGUARDIAN_API_KEY` repository secret is set.
   See [docs/development/secret-scanning.md](docs/development/secret-scanning.md) for full details.

Alternatively open the repo in the dev container (`.devcontainer/`): it provides the .NET SDK,
Docker-outside-of-Docker and runs `dotnet restore` on creation.

## NuGet lock files (#190)

Every project except `FlowLedger.AppHost` commits a `packages.lock.json` (`RestorePackagesWithLockFile` in `Directory.Build.props`). CI and every
Dockerfile restore with `--locked-mode` (the Dockerfiles copy the lock files next to each `.csproj` before restoring), so a dependency that
changes, or is republished with different content, fails the build instead of slipping in. `dotnet restore --locked-mode` is the local check.

Refresh the locks whenever you change `Directory.Packages.props` or any `PackageReference` (otherwise restore fails with NU1004 or NU1403):

```bash
dotnet restore FlowLedger.slnx --force-evaluate
```

and commit the changed `packages.lock.json` files with the change. Dependabot's `nuget` ecosystem regenerates lock files in its own PRs.
Notes:

* `FlowLedger.AppHost` has no lock file: the Aspire dashboard/orchestration packages are chosen per OS, so one file cannot match every machine.
* `FlowLedger.Web` downloads the Blazor assets pack at the version the installed SDK bundles. `Directory.Build.targets` pins it
  (`AspNetCoreAssetsPackVersion`) to the aspnet runtime image version so the lock is the same on every SDK; bump it together with the `aspnet`
  `FROM` line in the Dockerfiles, then refresh the locks.
* The first line of each Dockerfile (`# syntax=docker/dockerfile:1@sha256:...`) is pinned by digest. To refresh it:
  `docker buildx imagetools inspect docker/dockerfile:1` and copy the top-level `Digest:`; Dependabot does not update this line.

## Code style

- C# 14, nullable enabled, implicit usings, treat-warnings-as-errors.
- Run `dotnet format` before committing.
- PRs must pass `dotnet format --verify-no-changes` in CI.
- The build runs the .NET analyzers at `AnalysisMode=Recommended` with warnings as errors, plus unused usings (IDE0005), file-scoped namespaces (IDE0161) and banned APIs (`BannedSymbols.txt`, rule RS0030; every entry states why). Logging goes through `[LoggerMessage]` source-generated methods (CA1848).
- Options are registered with `AddOptions<T>().BindConfiguration(...)` plus validation and `ValidateOnStart()`; never `new XOptions()`, `new XOptions { }` or `?? new XOptions()` anywhere in `src` (an architecture test enforces it; the two allowlisted bootstrap sites say why).
- `TimeProvider` is a required argument in Domain and Application (no `TimeProvider.System` there); connectors take one so tests pass `TestClock.Provider`. Tests never read the wall clock (the ratchet baseline is empty).

### Meziantou.Analyzer (#113)

`Meziantou.Analyzer` is pinned in `Directory.Packages.props` and referenced for every non-test project from `Directory.Build.props` (tests are excluded:
fixture code parses ISO literals and builds throwaway regexes). It runs as a curated set, not at its defaults (about 3,200 warnings, almost all from rules
that duplicate or contradict earlier decisions). The `.editorconfig` block headed "Meziantou.Analyzer (#113)" lists what is on and, for every rule that
reports in this tree but is parked at `none`, the reason: MA0004 (ConfigureAwait), MA0048 (file name equals type name), MA0016 (collection
abstractions), MA0006 and MA0002 (string comparison, covered by CA1309/CA1307/CA1310), MA0051 (method length), MA0026 (TODO comments), MA0046
(event delegate shape).

On, and clean: MA0009 regex match timeout (every `Regex`/`[GeneratedRegex]` takes a one-second timeout), MA0011 `IFormatProvider`/culture on `Parse` and
`ToLower` (config and wire values parse with `CultureInfo.InvariantCulture`), MA0023 `ExplicitCapture` or named groups, MA0074, MA0061, MA0054, MA0132,
MA0134, MA0158 (`System.Threading.Lock` for lock gates) and MA0008. Turning another rule on means fixing all of its sites in the same change; a package
bump that starts reporting a new rule must park it (with a reason) or fix it. Roslynator was not measured.

### Suppression policy

- Fix the code first. Suppress only when the rule is wrong for that spot, and say why.
- Prefer a scoped `#pragma warning disable <ID> // <reason>` / `restore` pair or `[SuppressMessage(..., Justification = "...")]` over a file- or project-wide `.editorconfig` severity. A suppression without a reason is rejected in review.
- Parked rules live in `.editorconfig` (severity `suggestion`, with the reason in the comment block above them) and in `NoWarn` in `Directory.Build.props` (XML-doc quality warnings). Those lists may only shrink: flipping a rule to `warning` is that rule's cleanup milestone, and adding a new entry needs a linked issue.
- Count what exists with `git grep -c "pragma warning disable" -- src` and `git grep -c SuppressMessage -- src`; do not let the numbers grow without a reason in the PR.
- A banned API (`BannedSymbols.txt`) is suppressed only with `#pragma warning disable RS0030` and a reason, as `MigrateCommand` does for CLI output.

## Pull requests

- One feature or fix per PR.
- All new features need tests (see `tests/`).
- Bug fixes (`fix:` commits) must include a regression test that fails without the fix, or the PR must state why a test is not possible. The PR template asks for the test, the invariants touched and any migration.
- Follow the vertical slice pattern in `src/FlowLedger.Application/Features/`.
- Application idiom: one `*Handler` class per operation (a command or query) in the feature folder, registered in `Application/DependencyInjection.cs`, taking a `CancellationToken` on every async method. Do not add new `*Service` classes that bundle several operations; the existing ones (for example `BillSheetService`, `CategorizationService`) are migrated to handlers gradually. Use `ConnectorIds` constants, never bare connector name strings, and `TimeProvider` rather than `DateTime.UtcNow`.
- Architecture tests in `FlowLedger.Architecture.Tests` must pass.
- No sensitive data in logs or telemetry.

## Commit messages

Use conventional commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`.

## License

By contributing you agree that your contributions are licensed under AGPL-3.0-only.
