# The server and its web client in one image. Build from the repository root:
#   docker build -f backend/StealthDesk.Web.Server/Dockerfile -t stealthdesk-server .

# Build with the SDK; only the published output reaches the final image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# The project files first: the restore layer is reused until a dependency changes, not on every code change.
COPY .editorconfig Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY backend/StealthDesk.Web.Server/StealthDesk.Web.Server.csproj backend/StealthDesk.Web.Server/
COPY frontend/StealthDesk.Web.Client/StealthDesk.Web.Client.csproj frontend/StealthDesk.Web.Client/
COPY shared/StealthDesk.Branding/StealthDesk.Branding.csproj shared/StealthDesk.Branding/
COPY shared/StealthDesk.Contracts/StealthDesk.Contracts.csproj shared/StealthDesk.Contracts/
COPY shared/StealthDesk.Core/StealthDesk.Core.csproj shared/StealthDesk.Core/
COPY shared/StealthDesk.Observability/StealthDesk.Observability.csproj shared/StealthDesk.Observability/
RUN dotnet restore backend/StealthDesk.Web.Server/StealthDesk.Web.Server.csproj

COPY backend/ backend/
COPY frontend/ frontend/
COPY shared/ shared/
RUN dotnet publish backend/StealthDesk.Web.Server/StealthDesk.Web.Server.csproj \
  --configuration Release --no-restore --output /app -p:UseAppHost=false

# The Windows agent the server hands out: one executable carrying its own runtime. Built in parallel with the server.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS agent
WORKDIR /src
COPY .editorconfig Directory.Build.props Directory.Build.targets Directory.Packages.props ./
COPY agent/StealthDesk.Agent/StealthDesk.Agent.csproj agent/StealthDesk.Agent/
COPY agent/StealthDesk.Agent.Core/StealthDesk.Agent.Core.csproj agent/StealthDesk.Agent.Core/
COPY agent/StealthDesk.Agent.Windows/StealthDesk.Agent.Windows.csproj agent/StealthDesk.Agent.Windows/
COPY shared/StealthDesk.Branding/StealthDesk.Branding.csproj shared/StealthDesk.Branding/
COPY shared/StealthDesk.Contracts/StealthDesk.Contracts.csproj shared/StealthDesk.Contracts/
COPY shared/StealthDesk.Core/StealthDesk.Core.csproj shared/StealthDesk.Core/
COPY shared/StealthDesk.Hosting/StealthDesk.Hosting.csproj shared/StealthDesk.Hosting/
COPY shared/StealthDesk.Observability/StealthDesk.Observability.csproj shared/StealthDesk.Observability/
COPY shared/StealthDesk.Realtime/StealthDesk.Realtime.csproj shared/StealthDesk.Realtime/
RUN dotnet restore agent/StealthDesk.Agent/StealthDesk.Agent.csproj --runtime win-x64

COPY agent/ agent/
COPY shared/ shared/
RUN dotnet publish agent/StealthDesk.Agent/StealthDesk.Agent.csproj -p:PublishProfile=Windows --output /agent

# Run on the ASP.NET runtime alone.
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final

# Version and source, as OCI labels. The release build passes the version; a local build says "dev".
ARG VERSION=dev
ARG REVISION=unknown
LABEL org.opencontainers.image.title="StealthDesk server" \
      org.opencontainers.image.description="StealthDesk's server and web client." \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}" \
      org.opencontainers.image.source="https://github.com/fkauanGIT/StealthDesk" \
      org.opencontainers.image.licenses="Apache-2.0 with Commons Clause"

# curl, for the health check below: the runtime image has no HTTP client of its own.
RUN apt-get update \
  && apt-get install --yes --no-install-recommends curl \
  && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /app .
# Served at /downloads/agent/windows; install commands name its SHA-256.
COPY --from=agent /agent/StealthDesk.Agent.exe downloads/windows/
# The terms the image is shared under travel with it.
COPY LICENSE ./

# The image's own unprivileged "app" user, never root. ASP.NET listens on 8080, a port it may open.
USER $APP_UID
EXPOSE 8080

# Healthy once the server answers and its database does; starting includes the migrations, hence the grace period.
HEALTHCHECK --interval=30s --timeout=10s --start-period=60s --retries=3 \
  CMD curl --fail --silent --show-error http://localhost:8080/health/ready || exit 1

ENTRYPOINT ["dotnet", "StealthDesk.Web.Server.dll"]
