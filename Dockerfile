FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./
RUN dotnet publish Platform.Host/Platform.Host.csproj -c Release -o /app/platform \
    && dotnet publish Admin.Api/Admin.Api.csproj -c Release -o /app/admin \
    && dotnet publish Db.Migrator/Db.Migrator.csproj -c Release -o /app/migrate \
    && dotnet publish Provider.Mock/Provider.Mock.csproj -c Release -o /app/provider-mock \
    && dotnet publish ObjectStorage.FaultProxy/ObjectStorage.FaultProxy.csproj -c Release -o /app/object-storage-fault-proxy \
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
RUN apt-get update && apt-get install -y --no-install-recommends curl ca-certificates \
    && install -d /usr/share/postgresql-common/pgdg \
    && curl --fail --silent --show-error https://www.postgresql.org/media/keys/ACCC4CF8.asc \
        -o /usr/share/keyrings/postgresql.asc \
    && echo "deb [signed-by=/usr/share/keyrings/postgresql.asc] https://apt.postgresql.org/pub/repos/apt noble-pgdg main" \
        > /etc/apt/sources.list.d/pgdg.list \
    && apt-get update \
    && apt-get install -y --no-install-recommends postgresql-client-17 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/admin ./admin
RUN mkdir -p /var/lib/scalaapi/backups
EXPOSE 5001
ENTRYPOINT ["dotnet", "admin/Admin.Api.dll"]

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS migrate
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends libgssapi-krb5-2 \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/migrate ./migrate
COPY deploy/orleans-postgres-schema.sql ./migrations/000-orleans.sql
COPY deploy/migrations/ ./migrations/
ENTRYPOINT ["dotnet", "migrate/Db.Migrator.dll", "/app/migrations"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS provider-mock
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/provider-mock ./provider-mock
EXPOSE 8081
ENTRYPOINT ["dotnet", "provider-mock/ScalaAPI.Provider.Mock.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS object-storage-fault-proxy
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/object-storage-fault-proxy ./object-storage-fault-proxy
EXPOSE 9000 9002
ENTRYPOINT ["dotnet", "object-storage-fault-proxy/ScalaAPI.ObjectStorage.FaultProxy.dll"]
