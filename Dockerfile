# syntax=docker/dockerfile:1.7

# Build stage — .NET 10 SDK
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /build

# Copy project files first so `dotnet restore` is cacheable across source edits
COPY src/BabyBrain.Scrapers/BabyBrain.Scrapers.csproj BabyBrain.Scrapers/
COPY src/BabyBrain.Web/BabyBrain.Web.csproj BabyBrain.Web/
RUN dotnet restore BabyBrain.Web/BabyBrain.Web.csproj

# Copy the rest of the source
COPY src/ ./

RUN dotnet publish BabyBrain.Web/BabyBrain.Web.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# Runtime stage — Playwright .NET image (Chromium + OS deps pre-installed)
FROM mcr.microsoft.com/playwright/dotnet:v1.59.0-noble AS runtime
WORKDIR /app

# The Playwright .NET image ships with an older .NET runtime than the one we
# build against (net10.0). Install the ASP.NET 10 runtime alongside so
# `dotnet BabyBrain.Web.dll` finds Microsoft.NETCore.App 10.0.x at startup.
RUN apt-get update \
    && apt-get install -y --no-install-recommends wget ca-certificates \
    && wget -q https://packages.microsoft.com/config/ubuntu/24.04/packages-microsoft-prod.deb -O /tmp/ms-prod.deb \
    && dpkg -i /tmp/ms-prod.deb \
    && rm /tmp/ms-prod.deb \
    && apt-get update \
    && apt-get install -y --no-install-recommends aspnetcore-runtime-10.0 \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_RUNNING_IN_CONTAINER=true \
    DOTNET_NOLOGO=1 \
    BABYBRAIN_DB_PATH=/data/babybrain.db

# /data is mounted as a volume at runtime so the SQLite DB survives rebuilds
RUN mkdir -p /data

COPY --from=build /app/publish ./

EXPOSE 8080
ENTRYPOINT ["dotnet", "BabyBrain.Web.dll"]
