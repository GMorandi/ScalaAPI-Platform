FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY src/ ./
RUN dotnet publish Garnet.Host/Garnet.Host.csproj -c Release -o /app/platform
RUN dotnet publish Admin.Api/Admin.Api.csproj -c Release -o /app/admin

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/platform ./platform
COPY --from=build /app/admin ./admin
RUN mkdir -p /var/run/sub2api
EXPOSE 5000 5001
ENTRYPOINT ["dotnet", "platform/Garnet.Host.dll"]
