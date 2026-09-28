# The load generator as a container on the compose network, so a run measures the stack and not
# the host's port forwarding. Built from the repository root; see docker-compose.loadtest.yml.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/shared/Blog.Contracts/Blog.Contracts.csproj src/shared/Blog.Contracts/
COPY tests/Blog.LoadTests/Blog.LoadTests.csproj      tests/Blog.LoadTests/

RUN dotnet restore tests/Blog.LoadTests/Blog.LoadTests.csproj

COPY src/shared/Blog.Contracts/ src/shared/Blog.Contracts/
COPY tests/Blog.LoadTests/      tests/Blog.LoadTests/

RUN dotnet publish tests/Blog.LoadTests/Blog.LoadTests.csproj -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/out ./

ENTRYPOINT ["dotnet", "Blog.LoadTests.dll"]
