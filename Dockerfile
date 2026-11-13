# syntax=docker/dockerfile:1
#
# Multi-stage build for the MuseLite hosted agent. The build context is the REPO ROOT (set via
# azure.yaml docker.context: .), so the sibling library projects are reachable — code-mode remote_build
# ships only the single service folder, which is why this app (server + two class libraries) needs
# container mode. Builds remotely in ACR (docker.remoteBuild) — no local Docker required.

# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore layer: copy only the .csproj files first so restore caches independently of source edits.
# The analyzer ProjectReference in each csproj is Condition="Exists(...)"-guarded and lives OUTSIDE this
# context, so it is simply dropped here (local builds still enforce it).
COPY MuseLite/MuseLite.csproj MuseLite/
COPY MuseLite.ApprovalPolicy/MuseLite.ApprovalPolicy.csproj MuseLite.ApprovalPolicy/
COPY MuseLite.DurableContext/MuseLite.DurableContext.csproj MuseLite.DurableContext/
RUN dotnet restore MuseLite/MuseLite.csproj

# Build + publish: copy the three project trees and publish the server (its ProjectReferences pull in the
# two libraries). Framework-dependent publish runs on the aspnet runtime image below.
COPY MuseLite/ MuseLite/
COPY MuseLite.ApprovalPolicy/ MuseLite.ApprovalPolicy/
COPY MuseLite.DurableContext/ MuseLite.DurableContext/
RUN dotnet publish MuseLite/MuseLite.csproj -c Release -o /app --no-restore

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app ./
# The hosted-agents platform probes :8088; container mode owns the port binding (replaces the code-mode
# ASPNETCORE_URLS env var). All other config comes from Azure App Configuration + the agent's env vars.
ENV ASPNETCORE_URLS=http://+:8088
EXPOSE 8088
ENTRYPOINT ["dotnet", "MuseLite.dll"]
