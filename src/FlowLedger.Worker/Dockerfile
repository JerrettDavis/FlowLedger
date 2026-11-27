# syntax=docker/dockerfile:1@sha256:572cebbc2b15e6f84c077742952674889cb63cd5e9b80abdffcbe587b6f19183
# Build context: repo root  (docker build -f src/FlowLedger.Worker/Dockerfile .)

# ── Stage 1: restore ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS restore
WORKDIR /src
# Locked restore everywhere below (the publish stage inherits it): a drifting dependency fails the build (#190).
ENV RestoreLockedMode=true

# Central build / package management files
COPY global.json                  global.json
COPY Directory.Build.props        Directory.Build.props
COPY Directory.Build.targets      Directory.Build.targets
COPY .editorconfig                .editorconfig
COPY Directory.Packages.props     Directory.Packages.props

# Project files for the Worker + its dependencies
COPY src/FlowLedger.Worker/FlowLedger.Worker.csproj src/FlowLedger.Worker/packages.lock.json src/FlowLedger.Worker/
COPY src/FlowLedger.Application/FlowLedger.Application.csproj src/FlowLedger.Application/packages.lock.json src/FlowLedger.Application/
COPY src/FlowLedger.Infrastructure/FlowLedger.Infrastructure.csproj src/FlowLedger.Infrastructure/packages.lock.json src/FlowLedger.Infrastructure/
COPY src/FlowLedger.Contracts/FlowLedger.Contracts.csproj src/FlowLedger.Contracts/packages.lock.json src/FlowLedger.Contracts/
COPY src/FlowLedger.Domain/FlowLedger.Domain.csproj src/FlowLedger.Domain/packages.lock.json src/FlowLedger.Domain/
COPY src/FlowLedger.SharedKernel/FlowLedger.SharedKernel.csproj src/FlowLedger.SharedKernel/packages.lock.json src/FlowLedger.SharedKernel/
COPY src/FlowLedger.ServiceDefaults/FlowLedger.ServiceDefaults.csproj src/FlowLedger.ServiceDefaults/packages.lock.json src/FlowLedger.ServiceDefaults/
COPY src/FlowLedger.Integrations.Abstractions/FlowLedger.Integrations.Abstractions.csproj src/FlowLedger.Integrations.Abstractions/packages.lock.json src/FlowLedger.Integrations.Abstractions/
COPY src/FlowLedger.Integrations.Mx/FlowLedger.Integrations.Mx.csproj src/FlowLedger.Integrations.Mx/packages.lock.json src/FlowLedger.Integrations.Mx/
COPY src/FlowLedger.Integrations.Simulated/FlowLedger.Integrations.Simulated.csproj src/FlowLedger.Integrations.Simulated/packages.lock.json src/FlowLedger.Integrations.Simulated/
COPY src/FlowLedger.Integrations.SimpleFin/FlowLedger.Integrations.SimpleFin.csproj src/FlowLedger.Integrations.SimpleFin/packages.lock.json src/FlowLedger.Integrations.SimpleFin/
COPY src/FlowLedger.Integrations.Quiltt/FlowLedger.Integrations.Quiltt.csproj src/FlowLedger.Integrations.Quiltt/packages.lock.json src/FlowLedger.Integrations.Quiltt/

RUN dotnet restore --locked-mode src/FlowLedger.Worker/FlowLedger.Worker.csproj

# ── Stage 2: publish ──────────────────────────────────────────────────────────
FROM restore AS publish
WORKDIR /src

COPY src/ src/
COPY BannedSymbols.txt BannedSymbols.txt

RUN dotnet publish src/FlowLedger.Worker/FlowLedger.Worker.csproj \
        --no-restore \
        -c Release \
        -o /app/publish \
        /p:UseAppHost=false

# ── Stage 3: runtime ──────────────────────────────────────────────────────────
# Even though the Worker is a Generic Host (no HTTP server), it references
# Aspire.StackExchange.Redis and other packages that transitively depend on the
# Microsoft.AspNetCore.App shared framework.  Use the aspnet image so that
# framework is present; the smaller dotnet/runtime image would be missing it.

# restic is a static Go binary; the chiseled image has no package manager or shell, so it is copied
# from the official image. The same stage pre-creates the data directories owned by the image's
# non-root user (APP_UID 1654 in the .NET 8+ images): Docker seeds a new named volume from the image
# path, so the volumes mounted at /data/objects and /data/backups come up writable.
FROM restic/restic:0.20.0@sha256:93999df80d4ac6cb148e82d2ef1fe6a81996f17ff535e772723538211b31555a AS data-dirs
RUN mkdir -p /data/objects /data/backups && chown 1654:1654 /data/objects /data/backups

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled-extra@sha256:c8dd08a50d8ffd19bd8da72edee05a7ac15b486a5b1869cf46fdb370bf1fbc76 AS runtime
WORKDIR /app

COPY --from=data-dirs /usr/bin/restic /usr/local/bin/restic
COPY --from=data-dirs --chown=1654:1654 /data /data
COPY --from=publish /app/publish .

# Build identity (release builds pass the pushed tag and the tagged commit; local builds leave them empty and the app falls back
# to its assembly version). Surfaced by GET /api/status and as OCI labels.
ARG VERSION=
ARG COMMIT=
ENV FLOWLEDGER_VERSION=$VERSION \
    FLOWLEDGER_COMMIT=$COMMIT
LABEL org.opencontainers.image.version=$VERSION \
      org.opencontainers.image.revision=$COMMIT \
      org.opencontainers.image.source="https://github.com/JerrettDavis/FlowLedger"

# Explicit non-root user (the base image already sets APP_UID=1654; stated here so it is auditable).
USER $APP_UID

ENTRYPOINT ["dotnet", "FlowLedger.Worker.dll"]
