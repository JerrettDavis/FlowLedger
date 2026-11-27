# syntax=docker/dockerfile:1
# Build context: repo root  (docker build -f src/FlowLedger.Api/Dockerfile .)

# ── Stage 1: restore ─────────────────────────────────────────────────────────
# Copy only the manifest files first so the restore layer is cached until a
# .csproj or central-package file changes.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404@sha256:8c6edfb26c85153f236d26efebd9ad349aec403583f343f201ee67838b23658d AS restore
WORKDIR /src

# Central build / package management files
COPY global.json                  global.json
COPY Directory.Build.props        Directory.Build.props
COPY Directory.Build.targets      Directory.Build.targets
COPY .editorconfig                .editorconfig
COPY Directory.Packages.props     Directory.Packages.props

# Project files needed to restore the API's dependency tree
COPY src/FlowLedger.Api/FlowLedger.Api.csproj                                         src/FlowLedger.Api/
COPY src/FlowLedger.Application/FlowLedger.Application.csproj                         src/FlowLedger.Application/
COPY src/FlowLedger.Infrastructure/FlowLedger.Infrastructure.csproj                   src/FlowLedger.Infrastructure/
COPY src/FlowLedger.Contracts/FlowLedger.Contracts.csproj                                 src/FlowLedger.Contracts/
COPY src/FlowLedger.Domain/FlowLedger.Domain.csproj                                   src/FlowLedger.Domain/
COPY src/FlowLedger.SharedKernel/FlowLedger.SharedKernel.csproj                       src/FlowLedger.SharedKernel/
COPY src/FlowLedger.ServiceDefaults/FlowLedger.ServiceDefaults.csproj                 src/FlowLedger.ServiceDefaults/
COPY src/FlowLedger.Integrations.Abstractions/FlowLedger.Integrations.Abstractions.csproj  src/FlowLedger.Integrations.Abstractions/
COPY src/FlowLedger.Integrations.Mx/FlowLedger.Integrations.Mx.csproj                 src/FlowLedger.Integrations.Mx/
COPY src/FlowLedger.Integrations.Simulated/FlowLedger.Integrations.Simulated.csproj   src/FlowLedger.Integrations.Simulated/
COPY src/FlowLedger.Integrations.SimpleFin/FlowLedger.Integrations.SimpleFin.csproj   src/FlowLedger.Integrations.SimpleFin/
COPY src/FlowLedger.Integrations.Quiltt/FlowLedger.Integrations.Quiltt.csproj         src/FlowLedger.Integrations.Quiltt/

RUN dotnet restore src/FlowLedger.Api/FlowLedger.Api.csproj

# ── Stage 2: publish ──────────────────────────────────────────────────────────
FROM restore AS publish
WORKDIR /src

# Copy the full source (everything not excluded by .dockerignore)
COPY src/ src/

RUN dotnet publish src/FlowLedger.Api/FlowLedger.Api.csproj \
        --no-restore \
        -c Release \
        -o /app/publish \
        /p:UseAppHost=false

# ── Stage 3: runtime ──────────────────────────────────────────────────────────
# noble-chiseled-extra adds only ICU + tzdata: there is NO shell, curl or wget. Docker healthchecks
# therefore use scripts/healthprobe (a static Go binary built below and copied in).
FROM golang:1.24-alpine@sha256:8bee1901f1e530bfb4a7850aa7a479d17ae3a18beb6e09064ed54cfd245b7191 AS healthprobe
WORKDIR /src
COPY scripts/healthprobe/main.go .
RUN CGO_ENABLED=0 go build -trimpath -ldflags="-s -w" -o /healthprobe main.go

# restic (static Go binary) is copied in for "Back up now" off-site copies; the same stage pre-creates
# the data directories owned by the image's non-root user (APP_UID 1654 in the .NET 8+ images) so
# named volumes mounted there come up writable.
FROM restic/restic:0.20.0@sha256:93999df80d4ac6cb148e82d2ef1fe6a81996f17ff535e772723538211b31555a AS data-dirs
RUN mkdir -p /data/objects /data/backups && chown 1654:1654 /data/objects /data/backups

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled-extra@sha256:c8dd08a50d8ffd19bd8da72edee05a7ac15b486a5b1869cf46fdb370bf1fbc76 AS runtime
WORKDIR /app

COPY --from=healthprobe /healthprobe /usr/local/bin/healthprobe
COPY --from=data-dirs /usr/bin/restic /usr/local/bin/restic
COPY --from=data-dirs --chown=1654:1654 /data /data

# chiseled images run as non-root (APP_UID=1654) by default
COPY --from=publish /app/publish .

# ASP.NET chiseled images listen on 8080 (HTTP) by default; TLS is terminated
# by the reverse proxy / compose network layer.
EXPOSE 8080

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

ENTRYPOINT ["dotnet", "FlowLedger.Api.dll"]
