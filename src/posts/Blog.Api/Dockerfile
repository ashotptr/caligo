FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY Directory.Packages.props ./
COPY src/posts/Blog.Api/Blog.Api.csproj                                 src/posts/Blog.Api/
COPY src/posts/Blog.Posts.Domain/Blog.Posts.Domain.csproj               src/posts/Blog.Posts.Domain/
COPY src/posts/Blog.Posts.Application/Blog.Posts.Application.csproj     src/posts/Blog.Posts.Application/
COPY src/posts/Blog.Posts.Infrastructure/Blog.Posts.Infrastructure.csproj src/posts/Blog.Posts.Infrastructure/
COPY src/shared/Blog.Contracts/Blog.Contracts.csproj                    src/shared/Blog.Contracts/
COPY src/shared/Blog.Contracts.Grpc/Blog.Contracts.Grpc.csproj          src/shared/Blog.Contracts.Grpc/
COPY src/shared/Blog.SharedKernel/Blog.SharedKernel.csproj              src/shared/Blog.SharedKernel/
COPY src/catalog/Blog.Catalog.Domain/Blog.Catalog.Domain.csproj         src/catalog/Blog.Catalog.Domain/
COPY src/catalog/Blog.Catalog.Application/Blog.Catalog.Application.csproj src/catalog/Blog.Catalog.Application/
COPY src/catalog/Blog.Catalog.Infrastructure/Blog.Catalog.Infrastructure.csproj src/catalog/Blog.Catalog.Infrastructure/

COPY src/shared/Blog.Observability/Blog.Observability.csproj                        src/shared/Blog.Observability/

RUN dotnet restore src/posts/Blog.Api/Blog.Api.csproj

COPY src/shared/ src/shared/
COPY src/posts/ src/posts/
COPY src/catalog/ src/catalog/
COPY src/streamingservice/ src/streamingservice/

RUN dotnet publish src/posts/Blog.Api/Blog.Api.csproj -c Release -o /app/out --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update  && apt-get install --no-install-recommends -y curl  && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/out ./

USER $APP_UID

ENTRYPOINT ["dotnet", "Blog.Api.dll"]
