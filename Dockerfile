# =============================================================================
# Verbara.Platform.Api Dockerfile
# =============================================================================
#
# !! IP-LEAK WARNING — read before changing the publish flags !!
#
# This image currently ships as portable .NET IL DLLs (NOT Native AOT). The
# /app directory contains ~108 .NET DLLs at runtime, 68 of which are Verbara.*
# (including all of the closed-source Verbara.Sdk.Pro packages). Anyone with
# `docker pull` access can extract + decompile them.
#
# Per ADR-0022 (docs/decisions/0022-platform-api-aot-shipping-path.md) every
# PUBLIC ghcr.io/verbara/platform/* image MUST eventually be a Native-AOT
# single-binary image. Two blockers remain before this Dockerfile can flip:
#
#   1. SignalR server-side dispatch (Services/PushToHubRelay.cs) uses
#      `IHubContext<THub, T>.Clients.get` which is [RequiresDynamicCode].
#      Plan: extract the SignalR Hub into a separate Verbara.Platform.Realtime
#      microservice (ADR-0022 Phase A).
#
#   2. EF Core DataProtection (Program.cs:515,523,525) uses reflection over
#      entity types. Plan: replace with a Dapper-based IXmlRepository
#      (ADR-0022 Phase B).
#
# Once both blockers are resolved, swap the build + final stages to the AOT
# pathway commented at the bottom of this file. UNTIL THEN this Dockerfile
# remains non-AOT and the resulting image MUST NOT be pushed to a public
# registry without the maintainer's explicit acknowledgment of the IP-leak
# trade-off.
# =============================================================================

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG CACHEBUST=1
COPY . .
# Authenticate to GitHub Packages NuGet for private Verbara.Sdk.Pro.* packages.
# The github source is declared in NuGet.Config; we inject credentials at restore
# time via `dotnet nuget update source` (NOT a static packageSourceCredentials
# block — %VAR% substitution is unreliable on .NET Core). CI passes the token as
# BuildKit secret `nuget_auth_token`; local Docker builds pass the maintainer's
# GITHUB_PACKAGES_PAT the same way. The local-nuget-feed source declared in
# NuGet.Config is not available inside the build context — NuGet ignores missing
# directories and falls through to `github` (private Pro packages) and
# `nuget.org` (everything else) cleanly. Build stage is intermediate; the final
# stage COPYs only /app, so any credentials written into NuGet.Config here are
# discarded with the build layer.
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
    dotnet publish src/Verbara.Platform.Api/Verbara.Platform.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
# Layer C in-process check (Pro v2.3.x ADR-0011) reads the running image's OCI
# manifest-list digest from the IMAGE_DIGEST env var, NOT from a file baked
# inside the image. Original two-pass build attempted to bake the digest into
# /etc/verbara-image-digest, but an OCI image cannot self-reference its own
# manifest digest (chicken-and-egg: pass-1 staging digest != pass-2 final
# digest because content differs). Pivot documented in ADR-0011 Status update.
#
# Operator-side wiring of IMAGE_DIGEST:
#   * Helm chart: api.image.digest value -> IMAGE_DIGEST env var on Deployment
#   * docker-compose: environment: IMAGE_DIGEST=sha256:... in compose template
#   * The digest value comes from verbara-website/data/authorized-digests.json
#     (the registry the Worker reads when issuing licenses with
#     AuthorizedImageDigests claims).
#
# When IMAGE_DIGEST is unset (local `dotnet run`, plain `docker run` for dev),
# Pro's ContainerImageDigest.ReadFromEnvironment() returns null -> permissive
# path applies -> license validation falls through to expiry check unchanged
# (matches ADR-0011 dev-mode semantics).
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "Verbara.Platform.Api.dll"]

# =============================================================================
# AOT shipping pathway (ADR-0022) — DO NOT UNCOMMENT until Phases A+B+C are done
# =============================================================================
# Replace the two FROM stages above with:
#
# FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# WORKDIR /src
# ARG CACHEBUST=1
# RUN apt-get update && apt-get install -y --no-install-recommends \
#     clang zlib1g-dev && rm -rf /var/lib/apt/lists/*
# COPY . .
# RUN --mount=type=secret,id=nuget_auth_token,required=false \
#     set -e; \
#     dotnet nuget remove source local 2>/dev/null || true; \
#     if [ -f /run/secrets/nuget_auth_token ]; then \
#         dotnet nuget update source github \
#             --username verbara \
#             --password "$(cat /run/secrets/nuget_auth_token)" \
#             --store-password-in-clear-text; \
#     fi; \
#     dotnet nuget locals all --clear; \
#     dotnet publish src/Verbara.Platform.Api/Verbara.Platform.Api.csproj \
#         -c Release -r linux-x64 --self-contained true \
#         -p:PublishAot=true -p:InvariantGlobalization=true \
#         -o /app
#
# # Runtime-deps image carries libc + libssl + ICU only — no CLR / no .NET
# # runtime. The published binary is a self-contained native ELF.
# FROM mcr.microsoft.com/dotnet/runtime-deps:10.0 AS final
# RUN apt-get update && apt-get install -y --no-install-recommends curl \
#     && rm -rf /var/lib/apt/lists/*
# WORKDIR /app
# COPY --from=build /app .
# EXPOSE 5000
# ENV ASPNETCORE_URLS=http://+:5000
# ENTRYPOINT ["./Verbara.Platform.Api"]
#
# Post-flip verification (must hold before tagging the image):
#   * `ls /app | wc -l` ≤ 5  (binary + maybe a libArgon2.so + license dir)
#   * `ls /app/*.dll 2>/dev/null | wc -l` == 0
#   * `file /app/Verbara.Platform.Api` reports `ELF 64-bit LSB executable`
#   * Image size ≤ 100 MB (runtime-deps + binary; vs ~250 MB aspnet-runtime)
# =============================================================================
