# ADR 0005 — Aspire for Dev Inner Loop, Docker Compose for Self-Hosting

## Context
We need two runtime modes: a fast inner loop for development and a complete stack for self-hosting and E2E tests.

## Decision
- .NET Aspire (AppHost) is used for the development inner loop. It orchestrates the API, Worker, Web, and backing services (Postgres, Redis) with service discovery and dashboard built in.
- Docker Compose (docker-compose.full.yml) is used for self-hosting and Playwright E2E tests. It produces a fully containerized stack without the Aspire tooling dependency.

## Consequences
- Positive: Aspire gives a fast, debuggable inner loop with no Docker overhead for day-to-day development.
- Positive: Compose gives a reproducible, CI-friendly full stack for E2E and deployment.
- Negative: Two orchestration configs to maintain; kept in sync manually.
