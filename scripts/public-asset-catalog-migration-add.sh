#!/bin/bash
export ASPNETCORE_ENVIRONMENT=Development
dotnet ef migrations add "$1" \
    --project src/modules/Palladin.Module.PublicAssetCatalog/Palladin.Module.PublicAssetCatalog.csproj \
    --startup-project src/Palladin.Api/Palladin.Api.csproj \
    --context PublicAssetCatalogDbWriteContext \
    --output-dir Infrastructure/Persistence/Migrations
