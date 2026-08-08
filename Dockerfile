FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./
RUN dotnet publish Platform.Host/Platform.Host.csproj -c Release -o /app/platform \
    && dotnet publish Admin.Api/Admin.Api.csproj -c Release -o /app/admin \
    && dotnet publish Db.Migrator/Db.Migrator.csproj -c Release -o /app/migrate \
    && dotnet publish Provider.Mock/Provider.Mock.csproj -c Release -o /app/provider-mock \
    && find . -type d \( -name bin -o -name obj \) -prune -exec rm -rf '{}' + \
    && rm -rf /root/.nuget/packages /root/.local/share/NuGet /root/.cache/NuGet

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS platform-silo
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/platform ./platform
RUN mkdir -p /var/run/scalaapi
EXPOSE 5000
ENTRYPOINT ["dotnet", "platform/ScalaAPI.Platform.Host.dll"]

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
COPY deploy/migrations/001-baseline.sql ./migrations/001-baseline.sql
COPY deploy/migrations/002-product-invariants.sql ./migrations/002-product-invariants.sql
COPY deploy/migrations/003-entity-registry.sql ./migrations/003-entity-registry.sql
COPY deploy/migrations/004-auth-sessions.sql ./migrations/004-auth-sessions.sql
COPY deploy/migrations/005-lease-ledger.sql ./migrations/005-lease-ledger.sql
COPY deploy/migrations/006-durable-holds.sql ./migrations/006-durable-holds.sql
COPY deploy/migrations/007-request-idempotency.sql ./migrations/007-request-idempotency.sql
COPY deploy/migrations/008-redeem-code-atomicity.sql ./migrations/008-redeem-code-atomicity.sql
COPY deploy/migrations/009-password-reset-tokens.sql ./migrations/009-password-reset-tokens.sql
COPY deploy/migrations/010-email-verification.sql ./migrations/010-email-verification.sql
COPY deploy/migrations/011-idempotency-response-replay.sql ./migrations/011-idempotency-response-replay.sql
COPY deploy/migrations/012-lease-pricing-snapshots.sql ./migrations/012-lease-pricing-snapshots.sql
COPY deploy/migrations/013-payment-webhooks.sql ./migrations/013-payment-webhooks.sql
COPY deploy/migrations/014-subscription-lifecycle.sql ./migrations/014-subscription-lifecycle.sql
COPY deploy/migrations/015-payment-webhook-recovery.sql ./migrations/015-payment-webhook-recovery.sql
COPY deploy/migrations/016-media-object-storage.sql ./migrations/016-media-object-storage.sql
ENTRYPOINT ["dotnet", "migrate/Db.Migrator.dll", "/app/migrations"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS provider-mock
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/provider-mock ./provider-mock
EXPOSE 8081
ENTRYPOINT ["dotnet", "provider-mock/ScalaAPI.Provider.Mock.dll"]
