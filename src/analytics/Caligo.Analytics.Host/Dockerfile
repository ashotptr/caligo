FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/analytics/Caligo.Analytics.Host/Caligo.Analytics.Host.csproj                     src/analytics/Caligo.Analytics.Host/
COPY src/analytics/Caligo.Analytics.Core/Caligo.Analytics.Core.csproj                     src/analytics/Caligo.Analytics.Core/
COPY src/analytics/Caligo.Analytics.Infrastructure/Caligo.Analytics.Infrastructure.csproj src/analytics/Caligo.Analytics.Infrastructure/
COPY src/shared/Caligo.Contracts/Caligo.Contracts.csproj                                  src/shared/Caligo.Contracts/
COPY src/shared/Caligo.Contracts.Grpc/Caligo.Contracts.Grpc.csproj                        src/shared/Caligo.Contracts.Grpc/

COPY src/shared/Caligo.Observability/Caligo.Observability.csproj                        src/shared/Caligo.Observability/

RUN dotnet restore src/analytics/Caligo.Analytics.Host/Caligo.Analytics.Host.csproj

COPY src/shared/ src/shared/
COPY src/analytics/ src/analytics/

RUN dotnet publish src/analytics/Caligo.Analytics.Host/Caligo.Analytics.Host.csproj -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update  && apt-get install --no-install-recommends -y curl  && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/out ./

USER $APP_UID

ENTRYPOINT ["dotnet", "Caligo.Analytics.Host.dll"]
