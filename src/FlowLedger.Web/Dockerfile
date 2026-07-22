# syntax=docker/dockerfile:1
# Build context: repo root  (docker build -f src/FlowLedger.Web/Dockerfile .)

# ── Stage 1: restore ─────────────────────────────────────────────────────────
FROM mcr.microsoft.com/dotnet/sdk:10.0.302 AS restore
WORKDIR /src

# Central build / package management files
COPY global.json                  global.json
COPY Directory.Build.props        Directory.Build.props
COPY Directory.Packages.props     Directory.Packages.props

# Project files for the Web server + its dependencies
COPY src/FlowLedger.Web/FlowLedger.Web.csproj                                         src/FlowLedger.Web/
COPY src/FlowLedger.Web.Client/FlowLedger.Web.Client.csproj                           src/FlowLedger.Web.Client/
COPY src/FlowLedger.Application/FlowLedger.Application.csproj                         src/FlowLedger.Application/
COPY src/FlowLedger.Infrastructure/FlowLedger.Infrastructure.csproj                   src/FlowLedger.Infrastructure/
COPY src/FlowLedger.Domain/FlowLedger.Domain.csproj                                   src/FlowLedger.Domain/
COPY src/FlowLedger.SharedKernel/FlowLedger.SharedKernel.csproj                       src/FlowLedger.SharedKernel/
COPY src/FlowLedger.ServiceDefaults/FlowLedger.ServiceDefaults.csproj                 src/FlowLedger.ServiceDefaults/
COPY src/FlowLedger.Integrations.Abstractions/FlowLedger.Integrations.Abstractions.csproj  src/FlowLedger.Integrations.Abstractions/
COPY src/FlowLedger.Integrations.Mx/FlowLedger.Integrations.Mx.csproj                 src/FlowLedger.Integrations.Mx/
COPY src/FlowLedger.Integrations.Simulated/FlowLedger.Integrations.Simulated.csproj   src/FlowLedger.Integrations.Simulated/

RUN dotnet restore src/FlowLedger.Web/FlowLedger.Web.csproj

# ── Stage 2: publish ──────────────────────────────────────────────────────────
FROM restore AS publish
WORKDIR /src

COPY src/ src/

# Note: --no-restore is intentionally absent here. When a separate `dotnet restore` stage
# precedes `dotnet publish`, the Blazor static-asset pipeline (microsoft.aspnetcore.app.internal.assets)
# resolves blazor.web.js only during the publish target resolution phase — not during the plain
# NuGet restore pass. Omitting --no-restore lets publish redo the asset-aware package resolution,
# which is a no-op for most packages (they're already cached) but ensures framework JS files
# (blazor.web.js, blazor.server.js) are copied into /app/publish/wwwroot/_framework/.
RUN dotnet publish src/FlowLedger.Web/FlowLedger.Web.csproj \
        -c Release \
        -o /app/publish \
        /p:UseAppHost=false

# ── Stage 3: runtime ──────────────────────────────────────────────────────────
# noble-chiseled-extra adds curl + a minimal shell so Docker healthchecks work
# while keeping the image hardened (non-root, distroless-like).
FROM mcr.microsoft.com/dotnet/aspnet:10.0.10-noble-chiseled-extra AS runtime
WORKDIR /app

COPY --from=publish /app/publish .

# ASP.NET chiseled images listen on 8080 (HTTP) by default.
EXPOSE 8080

ENTRYPOINT ["dotnet", "FlowLedger.Web.dll"]
