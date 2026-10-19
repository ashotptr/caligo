FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/shared/Caligo.Contracts/Caligo.Contracts.csproj src/shared/Caligo.Contracts/
COPY tests/Caligo.LoadTests/Caligo.LoadTests.csproj      tests/Caligo.LoadTests/

RUN dotnet restore tests/Caligo.LoadTests/Caligo.LoadTests.csproj

COPY src/shared/Caligo.Contracts/ src/shared/Caligo.Contracts/
COPY tests/Caligo.LoadTests/      tests/Caligo.LoadTests/

RUN dotnet publish tests/Caligo.LoadTests/Caligo.LoadTests.csproj -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime
WORKDIR /app

COPY --from=build /app/out ./

ENTRYPOINT ["dotnet", "Caligo.LoadTests.dll"]
