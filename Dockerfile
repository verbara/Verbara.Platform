FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
ARG CACHEBUST=1
COPY . .
RUN NUGET_FILE=$(find . -maxdepth 1 -iname "nuget.config" | head -1) \
    && dotnet nuget locals all --clear \
    && if [ -d local-nuget-feed ] && [ -n "$NUGET_FILE" ]; then \
        sed -i "s|/media/Data/Source/IPcom/local-nuget-feed/|/src/local-nuget-feed/|" "$NUGET_FILE"; \
    else \
        dotnet nuget remove source local 2>/dev/null || true; \
    fi \
    && dotnet publish src/Verbara.Platform.Api/Verbara.Platform.Api.csproj -c Release -o /app

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
