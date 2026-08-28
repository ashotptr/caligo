# Build context is the repository root, because the API now references sibling projects
# (Domain / Application / Infrastructure) and Directory.Packages.props, none of which are
# reachable from the project directory.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Project files first, on their own layer: restore then only re-runs when a dependency
# changes, not on every source edit.
COPY Directory.Packages.props ./
COPY src/posts/Blog.Api/Blog.Api.csproj                                 src/posts/Blog.Api/
COPY src/posts/Blog.Posts.Domain/Blog.Posts.Domain.csproj               src/posts/Blog.Posts.Domain/
COPY src/posts/Blog.Posts.Application/Blog.Posts.Application.csproj     src/posts/Blog.Posts.Application/
COPY src/posts/Blog.Posts.Infrastructure/Blog.Posts.Infrastructure.csproj src/posts/Blog.Posts.Infrastructure/
COPY src/shared/Blog.Contracts/Blog.Contracts.csproj                    src/shared/Blog.Contracts/
COPY src/shared/Blog.Contracts.Grpc/Blog.Contracts.Grpc.csproj          src/shared/Blog.Contracts.Grpc/

COPY src/shared/Blog.Observability/Blog.Observability.csproj                        src/shared/Blog.Observability/

RUN dotnet restore src/posts/Blog.Api/Blog.Api.csproj

COPY src/shared/ src/shared/
COPY src/posts/ src/posts/

RUN dotnet publish src/posts/Blog.Api/Blog.Api.csproj -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# The runtime image has no HTTP client, and a Docker Compose healthcheck runs *inside* the
# container so it needs one. Kubernetes probes do not - the kubelet makes the request itself.
RUN apt-get update  && apt-get install --no-install-recommends -y curl  && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/out ./

# The image ships with a non-root user; a web process has no reason to run as root.
USER $APP_UID

ENTRYPOINT ["dotnet", "Blog.Api.dll"]
