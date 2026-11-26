# Third-party notices

FlowLedger itself is licensed under AGPL-3.0-only (see [LICENSE](./LICENSE)).

It depends on third-party packages whose exact versions are pinned centrally in
[Directory.Packages.props](./Directory.Packages.props) and whose licences are recorded per
package in the SPDX SBOMs produced by the `Docker` workflow (`sbom-api`, `sbom-web`, `sbom-worker`
artifacts). Those SBOMs are the authoritative, version-accurate notice list; regenerate this
file from them when cutting a release.

Direct dependencies include .NET / ASP.NET Core / EF Core (MIT), Npgsql (PostgreSQL licence),
Quartz.NET (Apache-2.0), MudBlazor (MIT), OpenIddict (Apache-2.0), Serilog (Apache-2.0),
OpenTelemetry .NET (Apache-2.0), StackExchange.Redis (MIT) and the test stack (xUnit, FluentAssertions,
Testcontainers, Playwright). Verify against the SBOM before relying on this summary.
