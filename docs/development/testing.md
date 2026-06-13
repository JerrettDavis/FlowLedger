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

### E2E Tests (CI-Only)

Playwright browser tests. Requires the full stack running and `E2E_BASE_URL` set.

**Do not run locally unless you know what you're doing** — they steal mouse/keyboard focus on Windows.

```bash
E2E_BASE_URL=https://localhost:7000 dotnet test tests/FlowLedger.E2E.Tests
```

## Full Non-E2E Sweep

Run everything except browser tests:

```bash
dotnet test FlowLedger.slnx --filter "Category!=E2E" --configuration Release
```

## CI

CI runs `--filter "Category!=E2E"` automatically. E2E tests run in a separate pipeline stage with the full Docker Compose stack.
