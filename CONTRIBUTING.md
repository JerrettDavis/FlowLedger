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

## Code style

- C# 14, nullable enabled, implicit usings, treat-warnings-as-errors.
- Run `dotnet format` before committing.
- PRs must pass `dotnet format --verify-no-changes` in CI.

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
