# Testing Guide

## Test Categories

### Unit Tests

Fast, no external dependencies. Cover domain logic, application handlers, and worker jobs.

```bash
dotnet test tests/FlowLedger.Domain.Tests
dotnet test tests/FlowLedger.Application.Tests
dotnet test tests/FlowLedger.Worker.Tests
```

### Integration Tests

Require Docker (Testcontainers spins up Postgres automatically).

```bash
dotnet test tests/FlowLedger.Infrastructure.Tests
dotnet test tests/FlowLedger.Api.Tests
```

### BDD Tests

TinyBDD + xUnit. Plain-English scenarios in `.feature`-style specs.

```bash
dotnet test tests/FlowLedger.Bdd.Tests
```

### Architecture Tests

Enforce layer boundaries and dependency rules using NetArchTest.

```bash
dotnet test tests/FlowLedger.Architecture.Tests
```

### Performance Tests

Tagged `[Trait("Category","Perf")]`. Fast enough to run in the normal suite but filterable.

Run all perf tests:
```bash
dotnet test --filter Category=Perf
```

Included in the default sweep (they complete in well under a second).

### E2E Tests (CI-only)

Playwright browser tests that run against a live, fully composed stack. They use
headless Chromium and are gated by the `E2E_BASE_URL` environment variable.

**Do not run locally unless you know what you are doing** — Playwright in headed mode
steals mouse and keyboard focus on Windows, and the full compose stack must already be
running.

#### How the gate works

`E2ETestBase` reads `E2E_BASE_URL` at startup. When the variable is not set, every test
method exits immediately and is recorded as Passed (0 failures) by xUnit. No browser is
launched. This means the E2E suite is safe to include in a solution-wide test run — it
produces zero failures rather than errors when the stack is not up.

#### Environment variables

| Variable | Required | Default | Description |
|----------|----------|---------|-------------|
| `E2E_BASE_URL` | Yes | _(none)_ | Base URL of the Web UI that Playwright navigates. Example: `http://localhost:5002`. When unset, all E2E tests are skipped cleanly — no browser is launched. |
| `E2E_API_URL` | No | `http://localhost:5001` | Base URL of the API service. Used by `AccountsDataTests` and `ScreenshotCaptureTests` to POST `/api/connect` and `/api/sync` for data seeding before assertions. |
| `E2E_API_KEY` | No | `dev-local-key-not-for-production` | API key sent as `X-Api-Key` in seeding requests. Must match `Api__Key` on the running API service. In CI this is `ci-test-api-key-not-for-production`, matching the `API__KEY` value passed to `docker compose`. |
| `E2E_TENANT_ID` | No | `00000000-0000-0000-0000-000000000001` | Tenant ID sent as `X-Tenant-Id` in seeding requests. Must match `Api__TenantId` on the running API service. |

All four variables are supplied explicitly in CI so the seeding requests use the same
credentials as the compose stack. The compose stack is configured separately via
`POSTGRES_PASSWORD` and `API__KEY` (see `.env.example`).

#### CI flow (`.github/workflows/e2e.yml`)

The E2E workflow runs on every push and pull request to `main`/`master`:

1. Checks out the repo and sets up .NET 10.
2. Builds the solution in Release mode.
3. Starts the full compose stack in detached mode:
   ```bash
   docker compose --profile full up --build -d
   ```
   The workflow provides `POSTGRES_PASSWORD=ci_test_password` and
   `API__KEY=ci-test-api-key-not-for-production` directly to the compose call.
4. Polls `http://localhost:5001/alive` (up to 120 s) and `http://localhost:5002/` (up to
   90 s) until both services are healthy.
5. Installs Chromium via the Playwright install script bundled with the test output.
6. Runs the E2E suite:
   ```bash
   dotnet test tests/FlowLedger.E2E.Tests \
     --no-build --configuration Release \
     --filter "Category=E2E" \
     --logger "trx;LogFileName=e2e-results.trx"
   ```
   with the following env vars set for the test runner:
   - `E2E_BASE_URL=http://localhost:5002`
   - `E2E_API_URL=http://localhost:5001`
   - `E2E_API_KEY=ci-test-api-key-not-for-production` (matches `API__KEY` supplied to compose)
   - `E2E_TENANT_ID=00000000-0000-0000-0000-000000000001` (matches `Api__TenantId` in compose)
7. Publishes a `.trx` test report via `dorny/test-reporter`.
8. On failure: uploads Playwright traces and screenshots, and dumps the last 100 compose
   log lines for debugging.
9. Always tears down the stack: `docker compose --profile full down -v`.

#### Running locally (advanced — discouraged)

If you must run E2E tests on your workstation:

1. Start the full stack:
   ```bash
   cp .env.example .env   # edit POSTGRES_PASSWORD if desired
   docker compose --profile full up --build -d
   ```
2. Wait until `http://localhost:5001/alive` returns HTTP 200.
3. Install Playwright browsers (one-time setup):
   ```powershell
   dotnet build tests/FlowLedger.E2E.Tests --configuration Release
   pwsh tests/FlowLedger.E2E.Tests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium
   ```
4. Run with the URL set:
   ```bash
   E2E_BASE_URL=http://localhost:5002 dotnet test tests/FlowLedger.E2E.Tests --filter "Category=E2E"
   ```
5. Tear down when done:
   ```bash
   docker compose --profile full down -v
   ```

## Full Non-E2E Sweep

Run everything except browser tests:

```bash
dotnet test FlowLedger.slnx --filter "Category!=E2E" --configuration Release
```

## CI

The standard CI pipeline (`ci.yml`) runs `--filter "Category!=E2E"` automatically.
E2E tests run in the separate `e2e.yml` pipeline with the full Docker Compose stack, as
described above.
