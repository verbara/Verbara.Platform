# =============================================================================
# Verbara.Platform.Api Dockerfile — Native AOT (ADR-0022 Phase D, 2026-05-20)
# =============================================================================
#
# This image ships a SELF-CONTAINED NATIVE-AOT single binary — NOT portable IL.
# The /app directory contains the native ELF executable + a handful of native
# .so deps; ZERO managed Verbara.* DLLs. Decompiling the closed-source
# Verbara.Sdk.Pro.* IP now requires native reverse-engineering (IDA Pro), not
# `ilspy` on a pulled image.
#
# How we got here (ADR-0022): Phase A extracted the SignalR Hub to
# Verbara.Platform.Realtime; Phase B replaced EF Core DataProtection with a
# Dapper IXmlRepository (later raw Npgsql); Phase D removed Dapper entirely
# (SDK + Pro + Platform → Verbara.Sdk.Data.Npgsql facade) — the last AOT
# blocker. `dotnet publish -p:PublishAot=true` now emits 0 IL2026/IL3050/IL207x
# diagnostics. See docs/operations/phase-d-validation/2026-05-19-pilot-aot-delta.md.
#
# !! BUILD PREREQUISITE !! The Pro packages consumed here MUST be the
# Dapper-free Verbara.Sdk.Pro.* 2.5.0-pro build. A Docker build restores Pro
# from the `github` NuGet source, so those Dapper-free packages must be PUBLISHED
# to GitHub Packages first (ADR-0022 Phase E cutover). Building against an older
# Pro that still depends on Dapper will reintroduce Dapper.dll into the closure
# and fail the AOT publish.
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG CACHEBUST=1
# Native AOT needs a C toolchain + zlib to link the self-contained ELF.
RUN apt-get update && apt-get install -y --no-install-recommends \
    clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
COPY . .
# Authenticate to GitHub Packages NuGet for private Verbara.Sdk.Pro.* packages
# (see ADR-0022 / NuGet.Config). Credentials are injected at restore time as a
# BuildKit secret and discarded with this intermediate build layer.
RUN --mount=type=secret,id=nuget_auth_token,required=false \
    set -e; \
    dotnet nuget remove source local 2>/dev/null || true; \
    if [ -f /run/secrets/nuget_auth_token ]; then \
        dotnet nuget update source github \
            --username verbara \
            --password "$(cat /run/secrets/nuget_auth_token)" \
            --store-password-in-clear-text; \
    fi; \
    dotnet nuget locals all --clear; \
    dotnet publish src/Verbara.Platform.Api/Verbara.Platform.Api.csproj \
        -c Release -r linux-x64 --self-contained true \
        -p:PublishAot=true -p:InvariantGlobalization=true \
        -o /app

# runtime-deps carries libc + libssl + (optionally) ICU only — no CLR, no .NET
# runtime. The published binary is a self-contained native ELF.
FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# Layer C in-process check (Pro ADR-0011) reads the running image's OCI
# manifest-list digest from the IMAGE_DIGEST env var (set by the Helm chart's
# api.image.digest / the compose template). When unset (local `docker run`,
# dev), Pro's ContainerImageDigest.ReadFromEnvironment() returns null and
# license validation falls through to the expiry check (dev-mode semantics).

# Build provenance. Without these, a running container cannot be traced back to
# the commit it was built from — `docker inspect` shows only the base image's
# inherited labels, so "is my lab current?" is unanswerable from image metadata
# alone. Pass them at build time:
#   docker build --build-arg VCS_REF=$(git rev-parse --short HEAD) \
#                --build-arg BUILD_DATE=$(date -u +%Y-%m-%dT%H:%M:%SZ) .
ARG VCS_REF=unknown
ARG BUILD_DATE=unknown
ARG VERSION=unknown
LABEL org.opencontainers.image.revision="${VCS_REF}" \
      org.opencontainers.image.created="${BUILD_DATE}" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.title="Verbara Platform API" \
      org.opencontainers.image.source="https://github.com/verbara/Verbara.Platform"

EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["./Verbara.Platform.Api"]

# Post-build verification (must hold before tagging + pushing the image):
#   * `ls /app/*.dll 2>/dev/null | wc -l` == 0   (no managed DLLs)
#   * `file /app/Verbara.Platform.Api` reports `ELF 64-bit LSB ... executable`
#   * image size materially smaller than the prior ~250 MB aspnet-runtime image
