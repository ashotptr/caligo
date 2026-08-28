# Build context is the repository root: this service references sibling projects and the
# shared contracts, none of which are reachable from the project directory.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/notifications/Blog.Notifications.Host/Blog.Notifications.Host.csproj src/notifications/Blog.Notifications.Host/
COPY src/notifications/Blog.Notifications.Core/Blog.Notifications.Core.csproj src/notifications/Blog.Notifications.Core/
COPY src/shared/Blog.Contracts/Blog.Contracts.csproj                          src/shared/Blog.Contracts/

COPY src/shared/Blog.Observability/Blog.Observability.csproj                        src/shared/Blog.Observability/

RUN dotnet restore src/notifications/Blog.Notifications.Host/Blog.Notifications.Host.csproj

COPY src/shared/ src/shared/
COPY src/notifications/ src/notifications/

RUN dotnet publish src/notifications/Blog.Notifications.Host/Blog.Notifications.Host.csproj -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# The runtime image has no HTTP client, and a Docker Compose healthcheck runs *inside* the
# container so it needs one. Kubernetes probes do not - the kubelet makes the request itself.
RUN apt-get update  && apt-get install --no-install-recommends -y curl  && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/out ./

USER $APP_UID

ENTRYPOINT ["dotnet", "Blog.Notifications.Host.dll"]
