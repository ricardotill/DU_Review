# DU Review as a container: a Release build of the Web project, run as the non-root `app` user.
# Build and run with docker-compose.yml (PostgreSQL plus this app); see docs/handleiding.md, "Met Docker Compose".

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
# The Web build runs `npm ci` for Bootstrap and the fonts: take Node and npm from the official Node image
COPY --from=node:22-slim /usr/local/bin/node /usr/local/bin/node
COPY --from=node:22-slim /usr/local/lib/node_modules/npm /usr/local/lib/node_modules/npm
RUN ln -s /usr/local/lib/node_modules/npm/bin/npm-cli.js /usr/local/bin/npm

WORKDIR /repo
# Restore first, so a code change does not download all packages again
COPY src/ScoutingAutomation.Contracts/*.csproj src/ScoutingAutomation.Contracts/
COPY src/ScoutingAutomation.Domain/*.csproj src/ScoutingAutomation.Domain/
COPY src/ScoutingAutomation.Infrastructure/*.csproj src/ScoutingAutomation.Infrastructure/
COPY src/ScoutingAutomation.ServiceDefaults/*.csproj src/ScoutingAutomation.ServiceDefaults/
COPY src/ScoutingAutomation.Web/*.csproj src/ScoutingAutomation.Web/
RUN dotnet restore src/ScoutingAutomation.Web/ScoutingAutomation.Web.csproj

COPY src/ src/
RUN dotnet publish src/ScoutingAutomation.Web/ScoutingAutomation.Web.csproj -c Release -o /app --no-restore --nologo

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app .
# The points file is not published; this copy is the default, docker-compose.yml mounts the repository's file over it
COPY src/ScoutingAutomation.Web/opdrachtenpunten.csv /app/opdrachtenpunten.csv

# Everything the app writes lives in /data (a volume), owned by `app` so a new volume is writable.
# The login cookie keys go there too: without them every restart logs everyone out.
# libgssapi-krb5-2: the PostgreSQL driver looks for it at every connection and logs an error without it
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /data/storage /home/app/.aspnet/DataProtection-Keys \
    && chown -R app:app /data /home/app/.aspnet

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    Scouting__StorageRoot=/data/storage \
    Scouting__RuntimeSettingsFile=/data/runtime-settings.json \
    Scouting__KeysFile=/data/access-keys.json \
    Scouting__AssignmentPointsFile=/app/opdrachtenpunten.csv \
    ConnectionStrings__scoutingdb="Data Source=/data/scouting.db"

USER app
EXPOSE 8080
VOLUME ["/data", "/home/app/.aspnet/DataProtection-Keys"]
ENTRYPOINT ["dotnet", "ScoutingAutomation.Web.dll"]
