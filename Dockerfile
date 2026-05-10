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
# Bake the OCI manifest-list digest of THIS image into /etc/verbara-image-digest so
# Verbara.Sdk.Pro.Licensing's ContainerImageDigest.ReadFromEnvironment() can compare
# against LicenseKey.AuthorizedImageDigests at runtime (Pro v2.3.x Layer C, ADR-0011).
#
# The CI release workflow (.github/workflows/release.yml) runs a two-pass build:
#   pass 1 -> placeholder digest -> push to a -staging tag -> capture the resulting
#             manifest-list digest reported by docker/build-push-action.
#   pass 2 -> rebuild with --build-arg VERBARA_IMAGE_DIGEST=<sha256:...> from pass 1
#             -> push to the final release tag -> sign that final digest with cosign.
#
# When VERBARA_IMAGE_DIGEST is empty (local `docker build` with no --build-arg, dev
# scenarios) the file is empty -> ContainerImageDigest returns null -> permissive
# path applies (matches ADR-0011 dev-mode semantics).
ARG VERBARA_IMAGE_DIGEST=""
RUN echo "${VERBARA_IMAGE_DIGEST}" > /etc/verbara-image-digest && chmod 644 /etc/verbara-image-digest
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "Verbara.Platform.Api.dll"]
