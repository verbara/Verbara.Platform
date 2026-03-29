FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN if [ -d local-nuget-feed ]; then \
        sed -i "s|/media/Data/Source/IPcom/local-nuget-feed/|/src/local-nuget-feed/|" nuget.config; \
    else \
        dotnet nuget remove source local 2>/dev/null || true; \
    fi \
    && dotnet publish src/Asterisk.Platform.Api/Asterisk.Platform.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 5000
ENV ASPNETCORE_URLS=http://+:5000
ENTRYPOINT ["dotnet", "Asterisk.Platform.Api.dll"]
