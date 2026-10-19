FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY tests/Caligo.StreamLab/Caligo.StreamLab.csproj tests/Caligo.StreamLab/
COPY src/analytics/Caligo.Analytics.Infrastructure/Stores/ClickHouseMigrations/0004-lab.sql src/analytics/Caligo.Analytics.Infrastructure/Stores/ClickHouseMigrations/

RUN dotnet restore tests/Caligo.StreamLab/Caligo.StreamLab.csproj

COPY tests/Caligo.StreamLab/ tests/Caligo.StreamLab/

RUN dotnet publish tests/Caligo.StreamLab/Caligo.StreamLab.csproj -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

RUN apt-get update \
 && apt-get install --yes --no-install-recommends ca-certificates curl ffmpeg chromium fonts-dejavu-core \
 && curl -fsSL https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -o /tmp/microsoft.deb \
 && dpkg -i /tmp/microsoft.deb \
 && rm /tmp/microsoft.deb \
 && apt-get update \
 && apt-get install --yes --no-install-recommends libmsquic \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY --from=build /app/out ./

ENTRYPOINT ["dotnet", "Caligo.StreamLab.dll"]
