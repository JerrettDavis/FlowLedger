# Secret Scanning Setup

FlowLedger uses multiple layers of secret-leak prevention to ensure credentials, API keys,
and private keys never enter source control — especially important as this is a public repository.

## Layers of Protection

| Layer | Tool | When It Runs |
|-------|------|-------------|
| Pre-commit hooks | gitleaks + ggshield + detect-private-key | Before every local commit |
| CI (push/PR) | gitleaks-action | On every push and pull request |
| CI (push/PR) | GitGuardian ggshield | On every push and pull request (requires API key setup) |

## Local Setup (Required for Contributors)

### 1. Install pre-commit

```bash
pip install pre-commit
```

### 2. Install the hooks into your local repo

```bash
pre-commit install
```

This installs the hooks defined in `.pre-commit-config.yaml`. From now on, every `git commit`
will automatically run gitleaks, ggshield, and `detect-private-key` before the commit is created.

### 3. Run hooks against all files (optional first-time check)

```bash
pre-commit run --all-files
```

### 4. Install gitleaks locally (optional, for manual scans)

Download from https://github.com/gitleaks/gitleaks/releases or via package manager:

```bash
# macOS
brew install gitleaks

# Windows (winget)
winget install gitleaks

# Linux
# Download binary from releases page
```

Then scan manually:

```bash
gitleaks detect --source . --redact
```

## What the Hooks Do

- **gitleaks**: Scans staged files and commit diffs for 150+ secret patterns (API keys, tokens, private keys, connection strings)
- **ggshield**: GitGuardian's scanner — broader pattern coverage, cross-references with known leaked credential databases
- **detect-private-key**: Simple check for PEM-format private keys (`-----BEGIN ... KEY-----`)
- **check-added-large-files**: Prevents accidentally committing large binary blobs

## GitGuardian API Key Setup

GitGuardian provides free scanning for public repositories.

### Get a free API key

1. Sign up at https://dashboard.gitguardian.com (free tier available)
2. Go to **API** → **Personal Access Tokens**
3. Create a token with the **scan** scope
4. Copy the token

### Add it as a GitHub repository secret

1. Go to your GitHub repo → **Settings** → **Secrets and variables** → **Actions**
2. Click **New repository secret**
3. Name: `GITGUARDIAN_API_KEY`
4. Value: paste your token
5. Click **Add secret**

Once added, the GitGuardian CI workflow (`.github/workflows/gitguardian.yml`) will activate
automatically on the next push.

## Secret Storage Rules

**Secrets MUST NOT be committed to source control — ever.** Use these alternatives:

| Environment | Where to put secrets |
|-------------|---------------------|
| Local development | `.env` file (gitignored), or `dotnet user-secrets` |
| Production / staging | Environment variables, secrets manager (Vault, AWS Secrets Manager, Azure Key Vault) |
| CI/CD | GitHub Actions secrets (`secrets.MY_KEY`) |
| Docker | Environment variables passed at runtime, never baked into images |

### Files that are gitignored for this reason

```
.env
*.env
appsettings.*.local.json
secrets.json
*.pfx
*.p12
*.snk
*.user
**/.secrets/
```

`.env.example` is tracked (committed) and serves as a template — it must contain only placeholder
values, never real secrets.

## Reference

- See `docs/self-hosting/production.md` for secret-rotation procedures in production deployments
- [gitleaks documentation](https://github.com/gitleaks/gitleaks)
- [GitGuardian ggshield documentation](https://docs.gitguardian.com/ggshield-docs/introduction)
- [pre-commit documentation](https://pre-commit.com/)
