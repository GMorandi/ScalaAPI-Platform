FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./
RUN dotnet publish Garnet.Host/Garnet.Host.csproj -c Release -o /app/platform \
    && dotnet publish Admin.Api/Admin.Api.csproj -c Release -o /app/admin \
    && dotnet publish Db.Migrator/Db.Migrator.csproj -c Release -o /app/migrate \
    && find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf '{}' + \
    && rm -rf /root/.nuget/packages /root/.local/share/NuGet /root/.cache/NuGet

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS platform-silo
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/platform ./platform
RUN mkdir -p /var/run/sub2api
EXPOSE 5000
ENTRYPOINT ["dotnet", "platform/Sub2Api.Platform.Host.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS admin-api
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/admin ./admin
EXPOSE 5001
ENTRYPOINT ["dotnet", "admin/Admin.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS migrate
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/migrate ./migrate
COPY deploy/orleans-postgres-schema.sql ./migrations/000-orleans.sql
COPY deploy/migrations/001-business.sql ./migrations/001-business.sql
COPY deploy/migrations/002-safety-and-settlement.sql ./migrations/002-safety-and-settlement.sql
COPY deploy/migrations/003-migration-control-and-entities.sql ./migrations/003-migration-control-and-entities.sql
COPY deploy/migrations/004-cdc-hardening.sql ./migrations/004-cdc-hardening.sql
COPY deploy/migrations/005-cdc-credential-traceability.sql ./migrations/005-cdc-credential-traceability.sql
COPY deploy/migrations/006-migration-fence-audit.sql ./migrations/006-migration-fence-audit.sql
COPY deploy/migrations/007-schema-parity-and-cdc-ordering.sql ./migrations/007-schema-parity-and-cdc-ordering.sql
ENTRYPOINT ["dotnet", "migrate/Db.Migrator.dll", "/app/migrations"]
