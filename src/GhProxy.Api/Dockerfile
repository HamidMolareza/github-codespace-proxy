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

ARG XRAY_VERSION=latest

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0

RUN arch="$(dpkg --print-architecture)" \
    && case "$arch" in \
        amd64) xray_asset="Xray-linux-64.zip" ;; \
        arm64) xray_asset="Xray-linux-arm64-v8a.zip" ;; \
        *) echo "Unsupported architecture: $arch" >&2; exit 1 ;; \
    esac \
    && if [ "$XRAY_VERSION" = "latest" ]; then \
        xray_url="https://github.com/XTLS/Xray-core/releases/latest/download/${xray_asset}"; \
    else \
        xray_url="https://github.com/XTLS/Xray-core/releases/download/${XRAY_VERSION}/${xray_asset}"; \
    fi \
    && curl -fsSL "$xray_url" -o /tmp/xray.zip \
    && unzip /tmp/xray.zip -d /tmp/xray \
    && install -m 0755 /tmp/xray/xray /usr/local/bin/xray \
    && rm -rf /tmp/xray /tmp/xray.zip

COPY --from=build /app/publish .

VOLUME ["/app/data"]
EXPOSE 8080 8901 8902

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD bash -c "</dev/tcp/127.0.0.1/8080" || exit 1

ENTRYPOINT ["./GhProxy.Api"]
