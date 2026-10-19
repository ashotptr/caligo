FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/posts/Caligo.Api/Caligo.Api.csproj                                 src/posts/Caligo.Api/
COPY src/posts/Caligo.Posts.Domain/Caligo.Posts.Domain.csproj               src/posts/Caligo.Posts.Domain/
COPY src/posts/Caligo.Posts.Application/Caligo.Posts.Application.csproj     src/posts/Caligo.Posts.Application/
COPY src/posts/Caligo.Posts.Infrastructure/Caligo.Posts.Infrastructure.csproj src/posts/Caligo.Posts.Infrastructure/
COPY src/shared/Caligo.Contracts/Caligo.Contracts.csproj                    src/shared/Caligo.Contracts/
COPY src/shared/Caligo.Contracts.Grpc/Caligo.Contracts.Grpc.csproj          src/shared/Caligo.Contracts.Grpc/
COPY src/shared/Caligo.SharedKernel/Caligo.SharedKernel.csproj              src/shared/Caligo.SharedKernel/
COPY src/catalog/Caligo.Catalog.Domain/Caligo.Catalog.Domain.csproj         src/catalog/Caligo.Catalog.Domain/
COPY src/catalog/Caligo.Catalog.Application/Caligo.Catalog.Application.csproj src/catalog/Caligo.Catalog.Application/
COPY src/catalog/Caligo.Catalog.Infrastructure/Caligo.Catalog.Infrastructure.csproj src/catalog/Caligo.Catalog.Infrastructure/

COPY src/shared/Caligo.Observability/Caligo.Observability.csproj                        src/shared/Caligo.Observability/

RUN dotnet restore src/posts/Caligo.Api/Caligo.Api.csproj

COPY src/shared/ src/shared/
COPY src/posts/ src/posts/
COPY src/catalog/ src/catalog/
COPY src/streamingservice/ src/streamingservice/

RUN dotnet publish src/posts/Caligo.Api/Caligo.Api.csproj -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update  && apt-get install --no-install-recommends -y curl  && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/out ./

USER $APP_UID

ENTRYPOINT ["dotnet", "Caligo.Api.dll"]
