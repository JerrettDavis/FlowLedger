# Branch protection and required checks

Branch protection is a repository setting and cannot be enforced from files in the repo. Configure
it for `master` (Settings, Branches) so that Dependabot auto-merge and human merges only land after CI.

## Required status checks

- `Build & Test (.NET 10)` (ci.yml)
- `Dependency Vulnerability Gate` (security.yml)
- `Build & Scan (api)`, `Build & Scan (web)`, `Build & Scan (worker)` (docker.yml)
- `Compose smoke test` (docker.yml)
- `Playwright E2E Tests` (e2e.yml)
- `Dependency review` (dependency-review.yml)
- `Scan for secrets` (gitleaks.yml)

Also enable: require branches to be up to date, require a pull request before merging, and
"Allow auto-merge" (the Dependabot workflow calls `gh pr merge --auto`, which only merges once the
required checks above pass).

## Dependabot auto-merge policy

`.github/workflows/dependabot-auto-merge.yml` enables auto-merge only for semver patch and minor
updates (via `dependabot/fetch-metadata`). Major bumps are left for a human.

## Verifying

Whether these settings are actually applied cannot be checked from the repository. A maintainer
should confirm them in the GitHub UI after changing workflow or job names.
