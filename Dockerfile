FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY . .
RUN dotnet restore src/Palladin.Api/Palladin.Api.csproj
RUN dotnet publish src/Palladin.Api/Palladin.Api.csproj \
    --configuration Release \
    --no-restore \
    --output /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ARG SOURCE_REVISION=development

LABEL org.opencontainers.image.licenses="AGPL-3.0-only" \
      org.opencontainers.image.revision="$SOURCE_REVISION" \
      org.opencontainers.image.source="https://github.com/Palladin-io/palladin-net-backend"

ENV ASPNETCORE_URLS=http://+:8080

COPY --from=build /app/publish .

EXPOSE 8080
ENTRYPOINT ["dotnet", "Palladin.Api.dll"]
