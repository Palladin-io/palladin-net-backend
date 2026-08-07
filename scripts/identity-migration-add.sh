#!/bin/bash
# Design-time uses the app host, which runs the startup JWT-secret guard; Development supplies a
# valid dev secret and connection strings so `dotnet ef migrations add` builds the context.
export ASPNETCORE_ENVIRONMENT=Development
dotnet ef migrations add "$1" \
    --project src/modules/Identity/Palladin.Module.Identity/Palladin.Module.Identity.csproj \
    --startup-project src/Palladin.Api/Palladin.Api.csproj \
    --context IdentityDbWriteContext \
    --output-dir Infrastructure/Persistence/Migrations
