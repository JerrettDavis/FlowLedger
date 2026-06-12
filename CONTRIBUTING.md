# Contributing to FlowLedger

Thank you for considering contributing.

## Development setup

1. Install [.NET 10 SDK](https://dot.net/download).
2. Install Docker Desktop (for Aspire-managed Postgres and Redis).
3. Install the Aspire workload: `dotnet workload install aspire`.
4. Clone the repo and run `./eng/scripts/run.ps1` (Windows) or `./eng/scripts/run.sh` (Linux/macOS).

## Code style

- C# 14, nullable enabled, implicit usings, treat-warnings-as-errors.
- Run `dotnet format` before committing.
- PRs must pass `dotnet format --verify-no-changes` in CI.

## Pull requests

- One feature or fix per PR.
- All new features need tests (see `tests/`).
- Follow the vertical slice pattern in `src/FlowLedger.Application/Features/`.
- Architecture tests in `FlowLedger.Architecture.Tests` must pass.
- No sensitive data in logs or telemetry.

## Commit messages

Use conventional commits: `feat:`, `fix:`, `docs:`, `refactor:`, `test:`, `chore:`.

## License

By contributing you agree that your contributions are licensed under AGPL-3.0-only.
