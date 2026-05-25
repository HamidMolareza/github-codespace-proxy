ARG NODE_BASE=mcr.microsoft.com/devcontainers/javascript-node:1-22-bookworm
FROM ${NODE_BASE} AS build
ARG NPM_REGISTRY=https://registry.npmjs.org/
WORKDIR /app

RUN npm config set registry "$NPM_REGISTRY" \
    && npm config set replace-registry-host always \
    && npm config set fetch-retries 5 \
    && npm config set fetch-retry-mintimeout 20000 \
    && npm config set fetch-retry-maxtimeout 120000

COPY frontend/package*.json ./
RUN npm ci

COPY frontend/ ./
RUN npm run build

FROM ${NODE_BASE} AS runtime
WORKDIR /app

ENV PORT=8080 \
    API_BASE_URL=http://backend:8080

COPY frontend/server.mjs ./server.mjs
COPY --from=build /app/dist ./dist

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD node -e "fetch('http://127.0.0.1:8080/').then(r => process.exit(r.ok ? 0 : 1)).catch(() => process.exit(1))"

CMD ["node", "server.mjs"]
