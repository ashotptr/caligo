FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/analytics/Blog.Analytics.Host/Blog.Analytics.Host.csproj                     src/analytics/Blog.Analytics.Host/
COPY src/analytics/Blog.Analytics.Core/Blog.Analytics.Core.csproj                     src/analytics/Blog.Analytics.Core/
COPY src/analytics/Blog.Analytics.Infrastructure/Blog.Analytics.Infrastructure.csproj src/analytics/Blog.Analytics.Infrastructure/
COPY src/shared/Blog.Contracts/Blog.Contracts.csproj                                  src/shared/Blog.Contracts/
COPY src/shared/Blog.Contracts.Grpc/Blog.Contracts.Grpc.csproj                        src/shared/Blog.Contracts.Grpc/

COPY src/shared/Blog.Observability/Blog.Observability.csproj                        src/shared/Blog.Observability/

RUN dotnet restore src/analytics/Blog.Analytics.Host/Blog.Analytics.Host.csproj

COPY src/shared/ src/shared/
COPY src/analytics/ src/analytics/

RUN dotnet publish src/analytics/Blog.Analytics.Host/Blog.Analytics.Host.csproj -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update  && apt-get install --no-install-recommends -y curl  && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/out ./

USER $APP_UID

ENTRYPOINT ["dotnet", "Blog.Analytics.Host.dll"]
