ARG DOTNET_BUILD_BASE=mcr.microsoft.com/dotnet/sdk:10.0
ARG BACKEND_RUNTIME_BASE=node:22-bookworm-slim
FROM ${DOTNET_BUILD_BASE} AS build
ARG NUGET_RESTORE_SOURCE=https://package-mirror.liara.ir/repository/nuget/index.json
ARG UBUNTU_APT_MIRROR=http://archive.ubuntu.com/ubuntu
ARG HTTP_PROXY
ARG HTTPS_PROXY
ARG NO_PROXY
ARG http_proxy
ARG https_proxy
ARG no_proxy
WORKDIR /src

RUN set -eux; \
    if ! command -v dotnet >/dev/null 2>&1; then \
        if [ -f /etc/apt/sources.list.d/ubuntu.sources ]; then \
            sed -i \
                -e "s#http://archive.ubuntu.com/ubuntu#${UBUNTU_APT_MIRROR}#g" \
                -e "s#http://security.ubuntu.com/ubuntu#${UBUNTU_APT_MIRROR}#g" \
                /etc/apt/sources.list.d/ubuntu.sources; \
        fi; \
        apt-get update; \
        DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
            ca-certificates \
            dotnet-sdk-10.0; \
        rm -rf /var/lib/apt/lists/*; \
    fi

COPY nuget.config Directory.Build.props ./
COPY src/GhProxy.Api/GhProxy.Api.csproj src/GhProxy.Api/
RUN dotnet restore src/GhProxy.Api/GhProxy.Api.csproj \
    --configfile nuget.config \
    --source "${NUGET_RESTORE_SOURCE}" \
    -p:NuGetAudit=false \
    -r linux-x64

COPY . .
RUN dotnet publish src/GhProxy.Api/GhProxy.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    -r linux-x64 \
    --self-contained true \
    -p:UseAppHost=true \
    -p:NuGetAudit=false

FROM ${BACKEND_RUNTIME_BASE} AS runtime
ARG UBUNTU_APT_MIRROR=http://archive.ubuntu.com/ubuntu
ARG DEBIAN_APT_MIRROR=
ARG DEBIAN_SECURITY_MIRROR=
ARG HTTP_PROXY
ARG HTTPS_PROXY
ARG NO_PROXY
ARG http_proxy
ARG https_proxy
ARG no_proxy
WORKDIR /app

ARG XRAY_VERSION=latest
ARG GH_VERSION=2.75.0

ENV ASPNETCORE_URLS=http://+:8080 \
    DOTNET_EnableDiagnostics=0 \
    HOME=/app/data/home

RUN set -eux; \
    if [ -f /etc/apt/sources.list.d/ubuntu.sources ]; then \
        sed -i \
            -e "s#http://archive.ubuntu.com/ubuntu#${UBUNTU_APT_MIRROR}#g" \
            -e "s#http://security.ubuntu.com/ubuntu#${UBUNTU_APT_MIRROR}#g" \
            /etc/apt/sources.list.d/ubuntu.sources; \
    fi; \
    if [ -n "${DEBIAN_APT_MIRROR}" ] && [ -f /etc/apt/sources.list.d/debian.sources ]; then \
        sed -i \
            -e "s#http://deb.debian.org/debian#${DEBIAN_APT_MIRROR}#g" \
            -e "s#https://deb.debian.org/debian#${DEBIAN_APT_MIRROR}#g" \
            /etc/apt/sources.list.d/debian.sources; \
    fi; \
    if [ -n "${DEBIAN_SECURITY_MIRROR}" ] && [ -f /etc/apt/sources.list.d/debian.sources ]; then \
        sed -i \
            -e "s#http://deb.debian.org/debian-security#${DEBIAN_SECURITY_MIRROR}#g" \
            -e "s#https://deb.debian.org/debian-security#${DEBIAN_SECURITY_MIRROR}#g" \
            -e "s#http://security.debian.org/debian-security#${DEBIAN_SECURITY_MIRROR}#g" \
            -e "s#https://security.debian.org/debian-security#${DEBIAN_SECURITY_MIRROR}#g" \
            /etc/apt/sources.list.d/debian.sources; \
    fi; \
    if ! command -v curl >/dev/null 2>&1 || ! command -v unzip >/dev/null 2>&1 || ! command -v ssh >/dev/null 2>&1; then \
        apt-get update; \
        DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends \
            bash \
            ca-certificates \
            curl \
            libstdc++6 \
            openssh-client \
            tzdata \
            unzip \
            zlib1g; \
        for package in libicu76 libicu74 libicu72 libssl3t64 libssl3; do \
            if apt-cache show "$package" >/dev/null 2>&1; then \
                DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends "$package"; \
            fi; \
        done; \
        rm -rf /var/lib/apt/lists/*; \
    fi

RUN arch="$(dpkg --print-architecture)" \
    && case "$arch" in \
        amd64) gh_arch="amd64" ;; \
        arm64) gh_arch="arm64" ;; \
        *) echo "Unsupported architecture: $arch" >&2; exit 1 ;; \
    esac \
    && curl -fsSL "https://github.com/cli/cli/releases/download/v${GH_VERSION}/gh_${GH_VERSION}_linux_${gh_arch}.tar.gz" -o /tmp/gh.tar.gz \
    && tar -xzf /tmp/gh.tar.gz -C /tmp \
    && install -m 0755 "/tmp/gh_${GH_VERSION}_linux_${gh_arch}/bin/gh" /usr/local/bin/gh \
    && rm -rf "/tmp/gh_${GH_VERSION}_linux_${gh_arch}" /tmp/gh.tar.gz

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

RUN mkdir -p /app/data/home/.ssh /app/data/xray /app/data/logs

VOLUME ["/app/data"]
EXPOSE 8080 8910

HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
    CMD bash -c "</dev/tcp/127.0.0.1/8080" || exit 1

ENTRYPOINT ["./GhProxy.Api"]
