FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0-alpine-aot AS build

ARG TARGETARCH
ARG VERSION=0.0.0-local

RUN apk add --no-cache nodejs npm

WORKDIR /src
COPY global.json Directory.Build.props ./
COPY src/HyPanel.Shared/ src/HyPanel.Shared/
COPY src/HyPanel.Server/ src/HyPanel.Server/
RUN sed -i '/StuDev.AotAnywhere/d' src/HyPanel.Server/HyPanel.Server.csproj
COPY web/HyPanel.Web/package.json web/HyPanel.Web/package-lock.json web/HyPanel.Web/
RUN npm ci --prefix web/HyPanel.Web
COPY web/HyPanel.Web/ web/HyPanel.Web/

RUN case "$TARGETARCH" in \
      amd64) rid=linux-musl-x64 ;; \
      arm64) rid=linux-musl-arm64 ;; \
      *) echo "Unsupported architecture: $TARGETARCH" >&2; exit 1 ;; \
    esac \
    && dotnet publish src/HyPanel.Server/HyPanel.Server.csproj -c Release -r "$rid" --self-contained true \
      -p:Version="$VERSION" -p:InformationalVersion="$VERSION" \
      -p:IncludeSourceRevisionInInformationalVersion=false -o /publish \
    && rm -f /publish/*.dbg /publish/*.pdb /publish/*.json

FROM mcr.microsoft.com/dotnet/runtime-deps:10.0-alpine

RUN apk add --no-cache wget \
    && addgroup -S -g 10001 hypanel \
    && adduser -S -D -H -u 10001 -G hypanel hypanel \
    && mkdir -p /app /data \
    && chown hypanel:hypanel /data

COPY --from=build /publish/ /app/

RUN chmod 0755 /app/HyPanel.Server \
    && chown -R root:root /app

ENV ASPNETCORE_URLS=http://0.0.0.0:8080 \
    HYPANEL_DATA_DIR=/data \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1

WORKDIR /data
VOLUME ["/data"]
EXPOSE 8080
USER 10001:10001

HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD wget -q -T 3 -O /dev/null http://127.0.0.1:8080/health || exit 1

ENTRYPOINT ["/app/HyPanel.Server"]
