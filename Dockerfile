# syntax=docker/dockerfile:1.7
ARG APP_CONTEXT_PATH=.

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG APP_CONTEXT_PATH
ARG USE_LIBREMETAVERSE_GIT=false
ARG LIBREMETAVERSE_SOURCE=nuget
ARG LIBREMETAVERSE_GIT_REPO=https://github.com/opensim-stack/libremetaverse.git
ARG LIBREMETAVERSE_GIT_REF=master
WORKDIR /src

# Build source modes in this target:
# - nuget (default): use published packages
# - git: clone LIBREMETAVERSE_GIT_REPO at LIBREMETAVERSE_GIT_REF
# Backward compatibility: USE_LIBREMETAVERSE_GIT=true forces git mode when source is nuget.
RUN set -eux; \
    source="$LIBREMETAVERSE_SOURCE"; \
    if [ "$USE_LIBREMETAVERSE_GIT" = "true" ] && [ "$source" = "nuget" ]; then source="git"; fi; \
    [ "$source" != "local" ] || { \
      echo "LIBREMETAVERSE_SOURCE=local requires --target runtime-local and --build-context libremetaverse_src=<path>"; \
      exit 1; \
    }; \
    printf '%s' "$source" > /tmp/lmv_source; \
    if [ "$source" = "git" ]; then \
      git clone --depth 1 --branch "$LIBREMETAVERSE_GIT_REF" "$LIBREMETAVERSE_GIT_REPO" /libremetaverse; \
    fi

COPY ${APP_CONTEXT_PATH}/src/opensim-metaverse2mcp.csproj src/
RUN set -eux; \
    source="$(cat /tmp/lmv_source)"; \
    if [ "$source" = "git" ]; then \
      dotnet restore src/opensim-metaverse2mcp.csproj /p:UseLibreMetaverseGit=true; \
    else \
      dotnet restore src/opensim-metaverse2mcp.csproj; \
    fi

COPY ${APP_CONTEXT_PATH}/src/ src/
RUN dotnet publish src/opensim-metaverse2mcp.csproj -c Release -o /out \
    /p:UseAppHost=false \
    /p:UseLibreMetaverseGit=$( [ "$(cat /tmp/lmv_source)" = "nuget" ] && echo false || echo true ) \
    --no-restore

FROM --platform=$BUILDPLATFORM mcr.microsoft.com/dotnet/sdk:10.0 AS build-local
ARG APP_CONTEXT_PATH
WORKDIR /src

COPY ${APP_CONTEXT_PATH}/src/opensim-metaverse2mcp.csproj src/
COPY --from=libremetaverse_src . /libremetaverse/
RUN set -eux; \
    test -f /libremetaverse/LibreMetaverse/LibreMetaverse.csproj

RUN dotnet restore src/opensim-metaverse2mcp.csproj /p:UseLibreMetaverseGit=true
COPY ${APP_CONTEXT_PATH}/src/ src/
RUN dotnet publish src/opensim-metaverse2mcp.csproj -c Release -o /out \
    /p:UseAppHost=false \
    /p:UseLibreMetaverseGit=true \
    --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime-local
ARG APP_CONTEXT_PATH
WORKDIR /app

COPY --from=build-local /out/ /app/
COPY ${APP_CONTEXT_PATH}/lsl/ /app/lsl/
COPY ${APP_CONTEXT_PATH}/docker/entrypoint.sh /entrypoint.sh

RUN apt-get update \
    && apt-get install -y --no-install-recommends procps iputils-ping \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/linden/cache /workspace/state \
    && chmod -R a+rwx /app/linden /workspace/state

EXPOSE 8999
ENTRYPOINT ["/entrypoint.sh"]

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
ARG APP_CONTEXT_PATH
WORKDIR /app

COPY --from=build /out/ /app/
COPY ${APP_CONTEXT_PATH}/lsl/ /app/lsl/
COPY ${APP_CONTEXT_PATH}/docker/entrypoint.sh /entrypoint.sh

RUN apt-get update \
    && apt-get install -y --no-install-recommends procps iputils-ping \
    && rm -rf /var/lib/apt/lists/*

RUN mkdir -p /app/linden/cache /workspace/state \
    && chmod -R a+rwx /app/linden /workspace/state

EXPOSE 8999
ENTRYPOINT ["/entrypoint.sh"]
