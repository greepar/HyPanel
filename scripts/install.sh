#!/bin/sh
# HyPanel Agent installer.  This file is intentionally POSIX sh: it is used on
# small Linux distributions as well as macOS.
set -eu

fail() { printf '%s\n' "install.sh: $*" >&2; exit 1; }
note() { printf '%s\n' "install.sh: $*" >&2; }

if [ -z "${HYPANEL_INSTALL_ROOT:-}" ]; then
    [ "$(id -u)" = "0" ] || fail "must be run as root"
fi
: "${HYPANEL_PANEL_URL:?HYPANEL_PANEL_URL is required}"
: "${HYPANEL_ENROLLMENT_TOKEN:?HYPANEL_ENROLLMENT_TOKEN is required}"

case "$HYPANEL_ENROLLMENT_TOKEN" in *'
'*) fail "HYPANEL_ENROLLMENT_TOKEN must not contain a newline" ;; esac

validate_url() {
    value=$1
    case "$value" in
        https://?*) ;;
        http://localhost|http://localhost/*|http://localhost:*)
            [ "${HYPANEL_ALLOW_INSECURE_HTTP:-}" = 1 ] || fail "HTTP is permitted only with HYPANEL_ALLOW_INSECURE_HTTP=1"
            ;;
        *) fail "URL must be an absolute HTTPS URL (or explicitly allowed localhost HTTP)" ;;
    esac
    case "$value" in *'?'*|*'#'*|*' '*|*'@'*|*'"'*|*\\*) fail "URL contains unsafe characters" ;; esac
    authority=${value#*://}
    authority=${authority%%/*}
    [ -n "$authority" ] || fail "URL must contain a host"
    case "$value" in
        http://localhost:*)
            port=${authority#localhost:}
            case "$port" in ''|*[!0-9]*) fail "localhost HTTP URL has an invalid port" ;; esac
            ;;
    esac
}

origin_of() {
    scheme=${1%%://*}
    authority=${1#*://}
    authority=${authority%%/*}
    printf '%s://%s\n' "$scheme" "$authority"
}

validate_url "$HYPANEL_PANEL_URL"
PANEL_URL=${HYPANEL_PANEL_URL%/}
MANIFEST_URL=${HYPANEL_MANIFEST_URL:-"$PANEL_URL/api/releases/v1/manifest"}
validate_url "$MANIFEST_URL"
MANIFEST_ORIGIN=$(origin_of "$MANIFEST_URL")

OS=$(uname -s 2>/dev/null || true)
ARCH=$(uname -m 2>/dev/null || true)
case "$ARCH" in
    x86_64|amd64) ARCH=x64 ;;
    aarch64|arm64) ARCH=arm64 ;;
    *) fail "unsupported architecture: $ARCH (only x64 and arm64 are supported)" ;;
esac

IS_OPENWRT=0
[ -f /etc/openwrt_release ] && IS_OPENWRT=1
case "$OS" in
    Linux)
        LIBC=linux
        if [ "$IS_OPENWRT" = 1 ]; then
            LIBC=linux-musl
        elif [ -e /lib/ld-musl-x86_64.so.1 ] || [ -e /lib/ld-musl-aarch64.so.1 ] || ls /lib/ld-musl-* >/dev/null 2>&1; then
            LIBC=linux-musl
        elif command -v ldd >/dev/null 2>&1 && ldd --version 2>&1 | grep -i musl >/dev/null 2>&1; then
            LIBC=linux-musl
        fi
        RID="$LIBC-$ARCH"
        ;;
    Darwin) RID="osx-$ARCH" ;;
    *) fail "unsupported operating system: $OS" ;;
esac
case "$RID" in
    linux-x64|linux-arm64|linux-musl-x64|linux-musl-arm64|osx-x64|osx-arm64) ;;
    *) fail "unsupported runtime identifier: $RID" ;;
esac

if [ -n "${HYPANEL_INSTALL_ROOT:-}" ]; then
    INSTALL_ROOT=${HYPANEL_INSTALL_ROOT%/}
    [ -n "$INSTALL_ROOT" ] || fail "HYPANEL_INSTALL_ROOT must not be empty"
    case "$INSTALL_ROOT" in /*) ;; *) fail "HYPANEL_INSTALL_ROOT must be an absolute path" ;; esac
    # A redirected root is a staging/test installation.  Never install a
    # system service that points at a test path.
    STAGING_INSTALL=1
else
    STAGING_INSTALL=0
    case "$OS" in
        Linux) INSTALL_ROOT=/opt/hypanel/agent ;;
        Darwin) INSTALL_ROOT=/usr/local/libexec/hypanel-agent ;;
    esac
fi
AGENT_PATH="$INSTALL_ROOT/HyPanel.Agent"
case "$OS" in
    Linux) DATA_DIR=${HYPANEL_DATA_DIR:-/var/lib/hypanel-agent} ;;
    Darwin) DATA_DIR=${HYPANEL_DATA_DIR:-"/Library/Application Support/HyPanel"} ;;
esac
BOOTSTRAP_ENV="$DATA_DIR/bootstrap.env"

command -v curl >/dev/null 2>&1 || fail "curl is required"
TMPDIR_BASE=${TMPDIR:-/tmp}
TMP_MANIFEST=$(mktemp "$TMPDIR_BASE/hypanel-manifest.XXXXXX") || fail "mktemp failed"
TMP_ASSET=$(mktemp "$TMPDIR_BASE/hypanel-agent.XXXXXX") || fail "mktemp failed"
EXTRACT_DIR=$(mktemp -d "$TMPDIR_BASE/hypanel-extract.XXXXXX") || fail "mktemp failed"
STAGED_INSTALL=$(mktemp -d "$TMPDIR_BASE/hypanel-install.XXXXXX") || fail "mktemp failed"
BACKUP_INSTALL="${INSTALL_ROOT}.previous.$$"
trap 'rm -f "$TMP_MANIFEST" "$TMP_ASSET"; rm -rf "$EXTRACT_DIR" "$STAGED_INSTALL"' EXIT HUP INT TERM

note "detecting $RID and downloading release manifest"
# Do not follow redirects: a redirect could silently change the trusted origin.
curl --fail --silent --show-error --proto '=https,http' "$MANIFEST_URL" -o "$TMP_MANIFEST" || fail "could not download manifest"

validate_asset_fields() {
    file_name=$1
    file_size=$2
    file_sha=$3
    case "$file_name" in ''|.|..|*/*|*\\*|*[!A-Za-z0-9._-]*) fail "manifest contains an unsafe file name" ;; esac
    case "$file_size" in ''|*[!0-9]*|0) fail "manifest contains an invalid asset size" ;; esac
    [ "${#file_sha}" -eq 64 ] || fail "manifest contains an invalid SHA-256"
    case "$file_sha" in *[!0123456789abcdef]*) fail "manifest contains an invalid SHA-256" ;; esac
}

select_with_python() {
    python3 - "$TMP_MANIFEST" "$RID" <<'PY'
import json, re, sys

def no_duplicates(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("duplicate JSON key: " + key)
        result[key] = value
    return result

try:
    with open(sys.argv[1], "rb") as source:
        manifest = json.load(source, object_pairs_hook=no_duplicates)
    expected_rids = {
        "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64",
        "osx-x64", "osx-arm64", "win-x64", "win-arm64",
    }
    if not isinstance(manifest, dict) or set(manifest) != {"schemaVersion", "version", "publishedAt", "assets"}:
        raise ValueError("unexpected manifest fields")
    if type(manifest["schemaVersion"]) is not int or manifest["schemaVersion"] != 1:
        raise ValueError("unsupported manifest schema")
    if not isinstance(manifest["version"], str) or not manifest["version"]:
        raise ValueError("invalid manifest version")
    if not isinstance(manifest["publishedAt"], str) or not manifest["publishedAt"]:
        raise ValueError("invalid manifest publication time")
    assets = manifest["assets"]
    if not isinstance(assets, list) or len(assets) != len(expected_rids):
        raise ValueError("invalid assets")
    seen_rids, seen_names, selected = set(), set(), None
    for asset in assets:
        if not isinstance(asset, dict) or set(asset) != {"rid", "fileName", "sha256", "size"}:
            raise ValueError("invalid asset fields")
        rid, name, sha, size = asset["rid"], asset["fileName"], asset["sha256"], asset["size"]
        if not isinstance(rid, str) or not rid or rid in seen_rids:
            raise ValueError("duplicate or invalid RID")
        if not isinstance(name, str) or not re.fullmatch(r"[A-Za-z0-9][A-Za-z0-9._-]*", name) or name in seen_names:
            raise ValueError("unsafe or duplicate file name")
        if not isinstance(sha, str) or not re.fullmatch(r"[0-9a-f]{64}", sha):
            raise ValueError("invalid SHA-256")
        if type(size) is not int or size <= 0:
            raise ValueError("invalid asset size")
        seen_rids.add(rid); seen_names.add(name)
        if rid == sys.argv[2]: selected = asset
    if seen_rids != expected_rids:
        raise ValueError("assets do not contain the frozen RID set")
    if selected is None:
        raise ValueError("no asset for requested RID")
    print(selected["fileName"] + "\t" + str(selected["size"]) + "\t" + selected["sha256"])
except (OSError, ValueError, json.JSONDecodeError) as error:
    print("invalid release manifest: " + str(error), file=sys.stderr)
    sys.exit(1)
PY
}

select_with_jsonfilter() {
    command -v jsonfilter >/dev/null 2>&1 || fail "OpenWrt requires jsonfilter to validate the release manifest (python3 is unavailable)"
    schema=$(jsonfilter -i "$TMP_MANIFEST" -e '@.schemaVersion' 2>/dev/null || true)
    version=$(jsonfilter -i "$TMP_MANIFEST" -e '@.version' 2>/dev/null || true)
    published=$(jsonfilter -i "$TMP_MANIFEST" -e '@.publishedAt' 2>/dev/null || true)
    [ "$schema" = 1 ] && [ -n "$version" ] && [ -n "$published" ] || fail "invalid or unsupported release manifest"
    index=0; selected_count=0; selected_name=; selected_size=; selected_sha=; all_rids=' '; all_names=' '
    while [ "$index" -lt 256 ]; do
        object=$(jsonfilter -i "$TMP_MANIFEST" -e "@.assets[$index]" 2>/dev/null || true)
        [ -n "$object" ] || break
        rid=$(jsonfilter -i "$TMP_MANIFEST" -e "@.assets[$index].rid" 2>/dev/null || true)
        name=$(jsonfilter -i "$TMP_MANIFEST" -e "@.assets[$index].fileName" 2>/dev/null || true)
        size=$(jsonfilter -i "$TMP_MANIFEST" -e "@.assets[$index].size" 2>/dev/null || true)
        sha=$(jsonfilter -i "$TMP_MANIFEST" -e "@.assets[$index].sha256" 2>/dev/null || true)
        [ -n "$rid" ] || fail "manifest contains an invalid asset"
        validate_asset_fields "$name" "$size" "$sha"
        case "$all_rids" in *" $rid "*) fail "manifest contains a duplicate RID" ;; esac
        case "$all_names" in *" $name "*) fail "manifest contains a duplicate file name" ;; esac
        all_rids="$all_rids$rid "; all_names="$all_names$name "
        if [ "$rid" = "$RID" ]; then selected_count=$((selected_count + 1)); selected_name=$name; selected_size=$size; selected_sha=$sha; fi
        index=$((index + 1))
    done
    ninth=$(jsonfilter -i "$TMP_MANIFEST" -e '@.assets[8]' 2>/dev/null || true)
    [ "$index" -eq 8 ] && [ -z "$ninth" ] && [ "$selected_count" -eq 1 ] || fail "manifest must contain exactly the frozen eight RIDs"
    for frozen_rid in linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64 osx-x64 osx-arm64 win-x64 win-arm64; do
        case "$all_rids" in *" $frozen_rid "*) ;; *) fail "manifest does not contain the frozen RID set" ;; esac
    done
    printf '%s\t%s\t%s\n' "$selected_name" "$selected_size" "$selected_sha"
}

if command -v python3 >/dev/null 2>&1; then
    ASSET_INFO=$(select_with_python) || fail "release manifest validation failed"
elif [ "$IS_OPENWRT" = 1 ]; then
    ASSET_INFO=$(select_with_jsonfilter) || fail "release manifest validation failed"
else
    fail "python3 is required to validate the release manifest"
fi
IFS="$(printf '\t')" read -r ASSET_NAME ASSET_SIZE ASSET_SHA <<EOF
$ASSET_INFO
EOF
validate_asset_fields "$ASSET_NAME" "$ASSET_SIZE" "$ASSET_SHA"

ASSET_URL="$MANIFEST_ORIGIN/api/releases/v1/assets/$ASSET_NAME"
note "downloading and verifying $ASSET_NAME"
curl --fail --silent --show-error --proto '=https,http' "$ASSET_URL" -o "$TMP_ASSET" || fail "could not download asset"
ACTUAL_SIZE=$(wc -c < "$TMP_ASSET" | tr -d ' ')
[ "$ACTUAL_SIZE" = "$ASSET_SIZE" ] || fail "asset size verification failed"
if command -v sha256sum >/dev/null 2>&1; then
    ACTUAL_SHA=$(sha256sum "$TMP_ASSET" | awk '{print $1}')
elif command -v shasum >/dev/null 2>&1; then
    ACTUAL_SHA=$(shasum -a 256 "$TMP_ASSET" | awk '{print $1}')
elif command -v openssl >/dev/null 2>&1; then
    ACTUAL_SHA=$(openssl dgst -sha256 "$TMP_ASSET" | awk '{print $NF}')
else
    fail "no SHA-256 tool found (need sha256sum, shasum, or openssl)"
fi
[ "$ACTUAL_SHA" = "$ASSET_SHA" ] || fail "asset SHA-256 verification failed"

command -v tar >/dev/null 2>&1 || fail "tar is required to extract the verified agent archive"
# The release asset is a tar.gz archive.  Refuse every member other than the
# two documented root-level regular files before extraction.
tar -tzf "$TMP_ASSET" > "$EXTRACT_DIR/members" || fail "asset is not a valid tar.gz archive"
tar -tvzf "$TMP_ASSET" > "$EXTRACT_DIR/member-details" || fail "could not inspect agent archive"
case "$(cut -c 1 "$EXTRACT_DIR/member-details" | tr -d '\n')" in *[lh]*) fail "archive must not contain symlinks or hardlinks" ;; esac
agent_members=0
settings_members=0
while IFS= read -r member; do
    member=${member#./}
    case "$member" in
        '') ;;
        HyPanel.Agent) agent_members=$((agent_members + 1)) ;;
        appsettings.json) settings_members=$((settings_members + 1)) ;;
        *) fail "archive contains an unsafe or unsupported member: $member" ;;
    esac
done < "$EXTRACT_DIR/members"
[ "$agent_members" -eq 1 ] || fail "archive must contain exactly one root HyPanel.Agent"
[ "$settings_members" -le 1 ] || fail "archive contains duplicate appsettings.json"
tar -xzf "$TMP_ASSET" -C "$EXTRACT_DIR" || fail "could not extract verified agent archive"
[ -f "$EXTRACT_DIR/HyPanel.Agent" ] && [ ! -L "$EXTRACT_DIR/HyPanel.Agent" ] || fail "archive must contain a regular root HyPanel.Agent"
[ ! -L "$EXTRACT_DIR/appsettings.json" ] || fail "archive contains a symlink"
for extracted in "$EXTRACT_DIR"/*; do
    case "${extracted##*/}" in
        HyPanel.Agent|appsettings.json|members|member-details) ;;
        *) fail "archive extraction produced an unexpected file" ;;
    esac
done
cp "$EXTRACT_DIR/HyPanel.Agent" "$STAGED_INSTALL/HyPanel.Agent"
chmod 755 "$STAGED_INSTALL/HyPanel.Agent"
if [ -f "$EXTRACT_DIR/appsettings.json" ]; then
    cp "$EXTRACT_DIR/appsettings.json" "$STAGED_INSTALL/appsettings.json"
    chmod 644 "$STAGED_INSTALL/appsettings.json"
fi

if [ "$STAGING_INSTALL" = 1 ]; then
    mkdir -p "${INSTALL_ROOT%/*}"
    if [ -e "$INSTALL_ROOT" ]; then mv "$INSTALL_ROOT" "$BACKUP_INSTALL"; fi
    if ! mv "$STAGED_INSTALL" "$INSTALL_ROOT"; then
        [ -e "$BACKUP_INSTALL" ] && mv "$BACKUP_INSTALL" "$INSTALL_ROOT" || true
        fail "could not atomically install verified agent archive"
    fi
    rm -rf "$BACKUP_INSTALL"
    chmod 755 "$INSTALL_ROOT"
    note "verified agent archive installed at $AGENT_PATH (service registration and enrollment skipped for HYPANEL_INSTALL_ROOT)"
    exit 0
fi

mkdir -p "${INSTALL_ROOT%/*}" "$DATA_DIR"
chmod 700 "$DATA_DIR"
AGENT_USER=
if [ "$STAGING_INSTALL" = 0 ] && [ "$OS" = Linux ] && { command -v systemctl >/dev/null 2>&1 || command -v rc-service >/dev/null 2>&1; }; then
    AGENT_USER=hypanel-agent
    if ! id "$AGENT_USER" >/dev/null 2>&1; then
        if command -v useradd >/dev/null 2>&1; then
            useradd --system --home "$DATA_DIR" --shell /usr/sbin/nologin "$AGENT_USER" || fail "could not create service user"
        elif command -v adduser >/dev/null 2>&1; then
            adduser -S -H -h "$DATA_DIR" "$AGENT_USER" || fail "could not create service user"
        else
            fail "a service user is required but useradd/adduser is unavailable"
        fi
    fi
    chown "$AGENT_USER" "$DATA_DIR"
fi
umask 077
escape_env() { printf '%s' "$1" | sed 's/[\\`"$]/\\&/g'; }
{
    printf 'HYPANEL_PANEL_URL="%s"\n' "$(escape_env "$PANEL_URL")"
    printf 'HYPANEL_ENROLLMENT_TOKEN="%s"\n' "$(escape_env "$HYPANEL_ENROLLMENT_TOKEN")"
    printf 'HYPANEL_DATA_DIR="%s"\n' "$(escape_env "$DATA_DIR")"
} > "$BOOTSTRAP_ENV"
chmod 600 "$BOOTSTRAP_ENV"
[ -n "$AGENT_USER" ] && chown "$AGENT_USER" "$BOOTSTRAP_ENV"

service_stop() {
    [ "$STAGING_INSTALL" = 1 ] && return 0
    case "$OS" in
        Linux)
            if [ "$IS_OPENWRT" = 1 ] && [ -x /etc/init.d/hypanel-agent ]; then /etc/init.d/hypanel-agent stop || true
            elif command -v systemctl >/dev/null 2>&1; then systemctl stop hypanel-agent.service || true
            elif command -v rc-service >/dev/null 2>&1; then rc-service hypanel-agent stop || true
            fi ;;
        Darwin) launchctl bootout system /Library/LaunchDaemons/com.hypanel.agent.plist >/dev/null 2>&1 || true ;;
    esac
}
service_start() {
    [ "$STAGING_INSTALL" = 1 ] && return 0
    case "$OS" in
        Linux)
            if [ "$IS_OPENWRT" = 1 ]; then /etc/init.d/hypanel-agent start
            elif command -v systemctl >/dev/null 2>&1; then systemctl daemon-reload; systemctl enable --now hypanel-agent.service
            elif command -v rc-service >/dev/null 2>&1; then rc-service hypanel-agent start
            else fail "no supported Linux service manager found (systemd, OpenRC, or procd required)"
            fi ;;
        Darwin) launchctl bootstrap system /Library/LaunchDaemons/com.hypanel.agent.plist ;;
    esac
}

service_stop
HAD_OLD=0
if [ -e "$INSTALL_ROOT" ]; then mv "$INSTALL_ROOT" "$BACKUP_INSTALL"; HAD_OLD=1; fi
mv "$STAGED_INSTALL" "$INSTALL_ROOT"
chmod 755 "$INSTALL_ROOT"

case "$OS" in
    Linux)
        if [ "$IS_OPENWRT" = 1 ]; then
            cat > /etc/init.d/hypanel-agent <<EOF
#!/bin/sh /etc/rc.common
START=95
USE_PROCD=1
start_service() { . "$BOOTSTRAP_ENV"; export HYPANEL_PANEL_URL HYPANEL_ENROLLMENT_TOKEN HYPANEL_DATA_DIR; procd_open_instance; procd_set_param command "$AGENT_PATH"; procd_set_param respawn; procd_close_instance; }
EOF
            chmod 755 /etc/init.d/hypanel-agent
            /etc/init.d/hypanel-agent enable
        elif command -v systemctl >/dev/null 2>&1; then
            cat > /etc/systemd/system/hypanel-agent.service <<EOF
[Unit]
Description=HyPanel Agent
After=network-online.target
Wants=network-online.target
[Service]
Type=simple
EnvironmentFile=$BOOTSTRAP_ENV
ExecStart=$AGENT_PATH
Restart=on-failure
RestartSec=5
User=$AGENT_USER
Group=$AGENT_USER
[Install]
WantedBy=multi-user.target
EOF
        elif command -v rc-service >/dev/null 2>&1; then
            cat > /etc/init.d/hypanel-agent <<EOF
#!/sbin/openrc-run
name="HyPanel Agent"
command="$AGENT_PATH"
command_background=true
pidfile=/run/hypanel-agent.pid
command_user="$AGENT_USER:$AGENT_USER"
. "$BOOTSTRAP_ENV"
export HYPANEL_PANEL_URL HYPANEL_ENROLLMENT_TOKEN HYPANEL_DATA_DIR
EOF
            chmod 755 /etc/init.d/hypanel-agent
            rc-update add hypanel-agent default
        fi ;;
    Darwin)
        cat > /Library/LaunchDaemons/com.hypanel.agent.plist <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict><key>Label</key><string>com.hypanel.agent</string><key>ProgramArguments</key><array><string>/bin/sh</string><string>-c</string><string>. "$BOOTSTRAP_ENV"; exec "$AGENT_PATH"</string></array><key>RunAtLoad</key><true/><key>KeepAlive</key><true/></dict></plist>
EOF
        chmod 600 /Library/LaunchDaemons/com.hypanel.agent.plist ;;
esac

if ! service_start; then
    note "new agent failed to start; restoring previous binary"
    rm -rf "$INSTALL_ROOT"; [ "$HAD_OLD" = 1 ] && mv "$BACKUP_INSTALL" "$INSTALL_ROOT" && service_start || true
    fail "agent start failed; enrollment token remains in $BOOTSTRAP_ENV"
fi

elapsed=0
while [ "$elapsed" -lt 30 ]; do
    if [ -s "$DATA_DIR/credentials.json" ]; then
        printf 'HYPANEL_DATA_DIR="%s"\n' "$(escape_env "$DATA_DIR")" > "$BOOTSTRAP_ENV"
        chmod 600 "$BOOTSTRAP_ENV"
        rm -rf "$BACKUP_INSTALL"
        note "enrollment completed successfully"
        exit 0
    fi
    sleep 1
    elapsed=$((elapsed + 1))
done

note "agent did not create credentials.json within 30 seconds; restoring previous binary"
service_stop
rm -rf "$INSTALL_ROOT"
if [ "$HAD_OLD" = 1 ]; then mv "$BACKUP_INSTALL" "$INSTALL_ROOT"; service_start || true; fi
fail "enrollment failed or timed out; enrollment token remains in $BOOTSTRAP_ENV"
