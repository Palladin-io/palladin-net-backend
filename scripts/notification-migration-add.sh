#!/bin/bash
# Design-time uses the app host, which runs the startup JWT-secret guard; Development supplies a
# valid dev secret and connection strings so `dotnet ef migrations add` builds the context.
export ASPNETCORE_ENVIRONMENT=Development
dotnet ef migrations add "$1" \
    --project src/modules/OpenHost/Notification/Palladin.Module.Notification/Palladin.Module.Notification.csproj \
    --startup-project src/Palladin.Api/Palladin.Api.csproj \
    --context NotificationDbWriteContext \
    --output-dir Infrastructure/Persistence/Migrations
