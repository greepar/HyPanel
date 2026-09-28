#!/bin/sh
# Container entrypoint for the HyPanel Agent.  The Agent executable lives in the data volume, not in the image:
# the first start downloads it from the Panel (verified against the release manifest), and the Agent's own
# self-update replaces it there, so updates survive container restarts and re-creation.
set -eu

log() { printf '%s\n' "hypanel-agent: $*" >&2; }
fail() { log "$*"; exit 1; }

HYPANEL_DATA_DIR=${HYPANEL_DATA_DIR:-/data}
export HYPANEL_DATA_DIR
DATA_DIR=$HYPANEL_DATA_DIR
AGENT_PATH="$DATA_DIR/bin/hypanel-agent"
mkdir -p "$DATA_DIR/bin"
chmod 700 "$DATA_DIR"

# A token the container has not used before means "enrol as the node it was issued for" (a reinstall), so the
# previous identity and its state are dropped.  Restarting with the same token keeps the enrolled identity.
if [ -n "${HYPANEL_ENROLLMENT_TOKEN:-}" ]; then
    token_hash=$(printf '%s' "$HYPANEL_ENROLLMENT_TOKEN" | sha256sum | cut -d ' ' -f 1)
    if [ "$(cat "$DATA_DIR/.enrollment-token" 2>/dev/null || true)" != "$token_hash" ]; then
        [ -e "$DATA_DIR/credentials.json" ] && log "new enrollment token; replacing the previous Agent identity"
        for state_file in credentials.json state.json command-state.json usage-state.json agent-update-state.json revoked; do
            rm -f "$DATA_DIR/$state_file"
        done
        rm -rf "$DATA_DIR/services"
        printf '%s\n' "$token_hash" > "$DATA_DIR/.enrollment-token"
    fi
fi

# Same guard as the systemd unit: if an updated Agent dies before it can verify itself twice in a row,
# put the previous executable back.
if [ -f "$AGENT_PATH.previous" ] && grep -q '"status":"RestartPending"' "$DATA_DIR/agent-update-state.json" 2>/dev/null; then
    if [ -f "$AGENT_PATH.rollback-armed" ]; then
        log "updated Agent failed to start; restoring the previous version"
        mv -f "$AGENT_PATH" "$AGENT_PATH.failed" && mv -f "$AGENT_PATH.previous" "$AGENT_PATH" && rm -f "$AGENT_PATH.rollback-armed"
    else
        : > "$AGENT_PATH.rollback-armed"
    fi
fi

download_agent() {
    panel=${HYPANEL_PANEL_URL:-}
    [ -n "$panel" ] || panel=$(jq -r '.panelBaseUrl // empty' "$DATA_DIR/credentials.json" 2>/dev/null || true)
    [ -n "$panel" ] || fail "HYPANEL_PANEL_URL is required for the first start"
    panel=${panel%/}
    case "$panel" in
        https://?*) ;;
        http://localhost|http://localhost:*) [ "${HYPANEL_ALLOW_INSECURE_HTTP:-}" = 1 ] || fail "HTTP is permitted only with HYPANEL_ALLOW_INSECURE_HTTP=1" ;;
        *) fail "HYPANEL_PANEL_URL must be an HTTPS URL" ;;
    esac
    case "$panel" in *'?'*|*'#'*|*' '*|*'@'*|*'"'*|*\\*) fail "HYPANEL_PANEL_URL contains unsafe characters" ;; esac
    case "$(uname -m)" in
        x86_64|amd64) rid=linux-musl-x64 ;;
        aarch64|arm64) rid=linux-musl-arm64 ;;
        *) fail "unsupported architecture: $(uname -m)" ;;
    esac

    work=$(mktemp -d)
    trap 'rm -rf "$work"' EXIT
    log "downloading the Agent for $rid from $panel"
    # Redirects are not followed: they could silently change the trusted origin.
    curl --fail --silent --show-error --proto '=https,http' "$panel/api/releases/v1/manifest" -o "$work/manifest.json" ||
        fail "could not download the release manifest"
    info=$(jq -r --arg rid "$rid" '[.assets[]? | select(.rid == $rid)] | if length == 1 then .[0] | "\(.fileName) \(.size) \(.sha256)" else empty end' "$work/manifest.json" 2>/dev/null) ||
        fail "invalid release manifest"
    set -- $info
    [ "$#" = 3 ] || fail "the release manifest has no single asset for $rid"
    name=$1 size=$2 sha=$3
    case "$name" in ''|.*|*[!A-Za-z0-9._-]*) fail "the release manifest contains an unsafe file name" ;; esac
    case "$size" in ''|*[!0-9]*) fail "the release manifest contains an invalid size" ;; esac
    case "$sha" in *[!0-9a-f]*) fail "the release manifest contains an invalid SHA-256" ;; esac
    [ "${#sha}" = 64 ] || fail "the release manifest contains an invalid SHA-256"

    curl --fail --silent --show-error --proto '=https,http' "$panel/api/releases/v1/assets/$name" -o "$work/agent.tar.gz" ||
        fail "could not download $name"
    [ "$(wc -c < "$work/agent.tar.gz" | tr -d ' ')" = "$size" ] || fail "$name size verification failed"
    printf '%s  %s\n' "$sha" "$work/agent.tar.gz" | sha256sum -c -s || fail "$name SHA-256 verification failed"
    mkdir "$work/extract"
    tar -xzf "$work/agent.tar.gz" -C "$work/extract" || fail "could not extract $name"
    [ -f "$work/extract/hypanel-agent" ] && [ ! -L "$work/extract/hypanel-agent" ] || fail "$name does not contain hypanel-agent"
    chmod 700 "$work/extract/hypanel-agent"
    "$work/extract/hypanel-agent" --self-test >/dev/null || fail "the downloaded Agent failed its self-test"
    mv -f "$work/extract/hypanel-agent" "$AGENT_PATH.download"
    mv -f "$AGENT_PATH.download" "$AGENT_PATH"
    rm -rf "$work"
    trap - EXIT
}

[ -x "$AGENT_PATH" ] || download_agent

# Docker Desktop (macOS: linuxkit, Windows: WSL2) and OrbStack run containers in a Linux VM that only forwards
# ports something listens on, so nftables redirects never see hop-port traffic there: relay it in user space.
if [ -z "${HYPANEL_PORT_HOPPING:-}" ] && grep -qiE 'orbstack|linuxkit|microsoft' /proc/version 2>/dev/null; then
    HYPANEL_PORT_HOPPING=relay
    export HYPANEL_PORT_HOPPING
    log "running inside a Docker Desktop / OrbStack VM; port hopping uses the user-space relay"
fi
cd "$DATA_DIR"
exec "$AGENT_PATH"
