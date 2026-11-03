# DU Review as a container: a Release build of the Web project, run as the non-root `app` user.
# Run with docker-compose.yml (two replicas behind a proxy, on your own PostgreSQL server); see docs/handleiding-beheer.md, "Met Docker Compose".
# CI (.github/workflows/ci.yml) builds it for linux/amd64 and linux/arm64 and pushes it to ghcr.io/ricardotill/du_review.

# Node for the build machine, not the target: npm only fetches Bootstrap and the fonts
FROM --platform=$BUILDPLATFORM node:26-slim AS node

# The build stage always runs on the machine's own platform and cross-compiles for the target one,
# so an arm64 image needs no slow emulation; only the final stage is per architecture.
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
# Version shown by the app; CI passes the release tag (1.4.0) or 0.0.0-<commit> for a build of main
ARG VERSION=0.0.0-local
# The Web build runs `npm ci` for Bootstrap and the fonts: take Node and npm from the official Node image
COPY --from=node /usr/local/bin/node /usr/local/bin/node
COPY --from=node /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/npm
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm

WORKDIR /repo
# Restore first, so a code change does not download all packages again
COPY src/ScoutingAutomation.Contracts/*.csproj src/ScoutingAutomation.Contracts/
COPY src/ScoutingAutomation.Domain/*.csproj src/ScoutingAutomation.Domain/
COPY src/ScoutingAutomation.Infrastructure/*.csproj src/ScoutingAutomation.Infrastructure/
COPY src/ScoutingAutomation.ServiceDefaults/*.csproj src/ScoutingAutomation.ServiceDefaults/
COPY src/ScoutingAutomation.Web/*.csproj src/ScoutingAutomation.Web/
RUN dotnet restore src/ScoutingAutomation.Web/ScoutingAutomation.Web.csproj -a $TARGETARCH

COPY src/ src/
RUN dotnet publish src/ScoutingAutomation.Web/ScoutingAutomation.Web.csproj -c Release -a $TARGETARCH -o /app --no-restore --nologo \
    -p:Version=$VERSION

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
ARG VERSION=0.0.0-local
LABEL org.opencontainers.image.title="DU Review" \
      org.opencontainers.image.source="https://github.com/ricardotill/DU_Review" \
      org.opencontainers.image.version=$VERSION
WORKDIR /app
COPY --from=build /app .
# The points file is not published; this copy is the default, docker-compose.yml mounts the repository's file over it
COPY src/ScoutingAutomation.Web/opdrachtenpunten.csv /app/opdrachtenpunten.csv

# Everything the app writes lives in /data (a volume), owned by `app` so a new volume is writable.
# The login cookie keys live in the database.
# libgssapi-krb5-2: the PostgreSQL driver looks for it at every connection and logs an error without it
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /data/storage \
    && chown -R app:app /data

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Scouting__StorageRoot=/data/storage \
    Scouting__RuntimeSettingsFile=/data/runtime-settings.json \
    Scouting__AssignmentPointsFile=/app/opdrachtenpunten.csv \
    ConnectionStrings__scoutingdb="Data Source=/data/scouting.db"

USER app
EXPOSE 8080
VOLUME ["/data"]
ENTRYPOINT ["dotnet", "ScoutingAutomation.Web.dll"]
