# syntax=docker/dockerfile:1
# Build context: repository root.  docker build -f deploy/docker/Dockerfile .

# ---- Frontend: node + pnpm (via corepack) ----
FROM --platform=$BUILDPLATFORM node:24-alpine AS frontend
WORKDIR /src/frontend
ENV CI=true
COPY frontend/package.json frontend/pnpm-lock.yaml ./
RUN corepack enable && pnpm install --frozen-lockfile
COPY frontend/ ./
RUN pnpm build

# ---- Backend: dotnet sdk; cross-compiles for the target architecture ----
FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
ARG TARGETARCH
WORKDIR /src
COPY backend/global.json backend/Directory.Build.props backend/Directory.Packages.props backend/Vinette.sln backend/
COPY backend/src/Vinette.Api/Vinette.Api.csproj backend/src/Vinette.Api/
COPY backend/src/Vinette.Core/Vinette.Core.csproj backend/src/Vinette.Core/
COPY backend/src/Vinette.Mail/Vinette.Mail.csproj backend/src/Vinette.Mail/
RUN case "$TARGETARCH" in amd64) echo x64 > /arch ;; arm64) echo arm64 > /arch ;; *) echo "unsupported arch $TARGETARCH" >&2; exit 1 ;; esac \
    && dotnet restore backend/src/Vinette.Api/Vinette.Api.csproj -a "$(cat /arch)"
COPY backend/src/ backend/src/
RUN dotnet publish backend/src/Vinette.Api/Vinette.Api.csproj \
      -c Release -a "$(cat /arch)" --no-restore --no-self-contained \
      -p:UseAppHost=false -o /app
COPY --from=frontend /src/frontend/dist /app/wwwroot
# Writable data directory for the non-root user (chiseled images have no shell to create it).
RUN mkdir -p /data/keys

# ---- Runtime: chiseled (distroless-style), non-root ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled AS final
ARG VERSION=0.0.0-dev
ARG REVISION=unknown
LABEL org.opencontainers.image.title="Vinette" \
      org.opencontainers.image.description="Self-hosted web mail client" \
      org.opencontainers.image.source="https://github.com/F1nn17/vinette" \
      org.opencontainers.image.licenses="Apache-2.0" \
      org.opencontainers.image.version="${VERSION}" \
      org.opencontainers.image.revision="${REVISION}"
WORKDIR /app
COPY --from=backend /app ./
COPY --from=backend --chown=1654:1654 /data /data
ENV ASPNETCORE_HTTP_PORTS=8080 \
    VINETTE_DataProtection__KeysPath=/data/keys
USER 1654:1654
EXPOSE 8080
VOLUME /data
# No shell or curl in chiseled images: the app probes itself.
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
  CMD ["dotnet", "/app/Vinette.Api.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "/app/Vinette.Api.dll"]
