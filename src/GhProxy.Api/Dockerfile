FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

COPY src/GhProxy.Api/GhProxy.Api.csproj src/GhProxy.Api/
RUN dotnet restore src/GhProxy.Api/GhProxy.Api.csproj -p:NuGetAudit=false -r linux-x64

COPY . .
RUN dotnet publish src/GhProxy.Api/GhProxy.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    -r linux-x64 \
    --self-contained true \
    -p:UseAppHost=true \
    -p:NuGetAudit=false

FROM mcr.microsoft.com/devcontainers/javascript-node:1-22-bookworm AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish .

VOLUME ["/app/data"]
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD bash -c "</dev/tcp/127.0.0.1/8080" || exit 1

ENTRYPOINT ["./GhProxy.Api"]
