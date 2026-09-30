#!/bin/sh
set -eu

usage() {
    printf '%s\n' "Usage: $0 VERSION OUTPUT_DIR" >&2
    exit 2
}

[ "$#" -eq 2 ] || usage
VERSION=$1
OUTPUT_DIR=$2

case "$VERSION" in
    ''|*[!A-Za-z0-9._-]*) printf '%s\n' 'invalid version' >&2; exit 2 ;;
esac
# Windows runners provide python rather than python3.
if [ -z "${PYTHON:-}" ]; then
    if command -v python3 >/dev/null 2>&1; then PYTHON=python3; else PYTHON=python; fi
fi
sha256_of() {
    if command -v sha256sum >/dev/null 2>&1; then sha256sum "$1" | cut -d ' ' -f 1
    else shasum -a 256 "$1" | cut -d ' ' -f 1
    fi
}
# Extra `dotnet publish` arguments, e.g. a cross-compilation SysRoot for the old-glibc Linux builds.
EXTRA_PUBLISH_ARGS=${EXTRA_PUBLISH_ARGS:-}
# Highest GLIBC symbol version a glibc binary may require (Ubuntu 22.04 ships 2.35, Debian 12 2.36).
MAX_GLIBC=${MAX_GLIBC:-2.35}
"$PYTHON" - "$VERSION" <<'PY'
import re, sys
if not re.fullmatch(r'(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?', sys.argv[1]):
    raise SystemExit('invalid semantic version')
PY
case "$VERSION" in
    *-*) ASSEMBLY_VERSION=${VERSION%%-*} ;;
    *) ASSEMBLY_VERSION=$VERSION ;;
esac

REPO_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)
case "$OUTPUT_DIR" in
    /*) : ;;
    *) OUTPUT_DIR="$REPO_ROOT/$OUTPUT_DIR" ;;
esac
cd "$REPO_ROOT"

if [ "${AGENT_RIDS+x}" != x ]; then
    AGENT_RIDS='win-x64 win-arm64 osx-x64 osx-arm64 linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64'
fi
if [ "${SERVER_RIDS+x}" != x ]; then
    SERVER_RIDS='linux-x64 linux-arm64 linux-musl-x64 linux-musl-arm64'
fi

case "${SOURCE_DATE_EPOCH:-}" in
    '' ) SOURCE_DATE_EPOCH=$(git log -1 --format=%ct) ;;
    *[!0-9]*) printf '%s\n' 'SOURCE_DATE_EPOCH must be a non-negative integer epoch' >&2; exit 2 ;;
esac
PUBLISHED_AT=$(date -u -r "$SOURCE_DATE_EPOCH" '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u -d "@$SOURCE_DATE_EPOCH" '+%Y-%m-%dT%H:%M:%SZ')

rm -rf "$OUTPUT_DIR"
mkdir -p "$OUTPUT_DIR"

# Guards against toolchain regressions that only show up on the target machine: a static musl binary cannot load
# libssl (every HTTPS request fails), and a glibc binary built against a newer glibc will not start on older distros.
check_linux_binary() {
    rid=$1
    binary=$2
    case "$rid" in
        linux-musl-*)
            "$PYTHON" - "$binary" <<'PY' || {
import struct, sys
# A dynamically linked executable has a PT_INTERP program header naming the musl loader.
with open(sys.argv[1], "rb") as elf:
    header = elf.read(64)
    phoff, = struct.unpack_from("<Q", header, 0x20)
    phentsize, phnum = struct.unpack_from("<HH", header, 0x36)
    elf.seek(phoff)
    types = [struct.unpack("<I", elf.read(phentsize)[:4])[0] for _ in range(phnum)]
sys.exit(0 if 3 in types else 1)
PY
                printf '%s\n' "$binary must be dynamically linked against musl (a static binary cannot load libssl)" >&2
                exit 1
            } ;;
        linux-*)
            required=$(grep -ao 'GLIBC_[0-9][0-9.]*' "$binary" | sed 's/GLIBC_//' | sort -t . -k 1,1n -k 2,2n | tail -n 1)
            printf '%s\n' "$binary requires glibc $required"
            highest=$(printf '%s\n%s\n' "$required" "$MAX_GLIBC" | sort -t . -k 1,1n -k 2,2n | tail -n 1)
            [ "$highest" = "$MAX_GLIBC" ] || {
                printf '%s\n' "$binary requires glibc $required, above the supported $MAX_GLIBC" >&2
                exit 1
            } ;;
    esac
}

publish_and_package() {
    project=$1
    kind=$2
    rid=$3
    publish_dir="$REPO_ROOT/.release-publish/$kind-$rid"
    package_dir="$REPO_ROOT/.release-package/$kind-$rid"
    project_name=$(basename "$project" .csproj)
    binary_name=$project_name
    case "$rid" in win-*) binary_name="$binary_name.exe" ;; esac
    # Installed (and archived) as hypanel-agent / hypanel-server; publish output keeps the .NET project name.
    packaged_name="hypanel-$kind"
    case "$rid" in win-*) packaged_name="$packaged_name.exe" ;; esac
    archive_name="hypanel-$kind-$VERSION-$rid"

    rm -rf "$publish_dir" "$package_dir"
    mkdir -p "$package_dir"
    if [ "$kind" = agent ]; then
        case "$rid" in linux-*)
            wg_bundle_dir="$REPO_ROOT/.wireguard-build/$rid"
            sh scripts/build-wireguard-tool.sh "$rid" "$wg_bundle_dir"
            wg_tool_name="wg-1.0.20260223-$rid"
            cp "$wg_bundle_dir/wg" "$OUTPUT_DIR/$wg_tool_name"
            cp "$wg_bundle_dir/wireguard-tools-1.0.20260223.tar.xz" "$OUTPUT_DIR/"
            printf '%s\t%s\t%s\t%s\n' "$rid" "$wg_tool_name" "$(sha256_of "$OUTPUT_DIR/$wg_tool_name")" "$(wc -c < "$OUTPUT_DIR/$wg_tool_name" | tr -d ' ')" >> "$TOOLS_TSV"
            ;;
        esac
    fi
    # shellcheck disable=SC2086 # EXTRA_PUBLISH_ARGS is a list of arguments.
    dotnet publish "$project" -c Release -r "$rid" --self-contained true -o "$publish_dir" $EXTRA_PUBLISH_ARGS \
        -p:Version="$VERSION" -p:VersionPrefix="$ASSEMBLY_VERSION" \
        -p:FileVersion="$ASSEMBLY_VERSION.0" -p:InformationalVersion="$VERSION" \
        -p:IncludeSourceRevisionInInformationalVersion=false \
        -p:AgentBuildVersion="$VERSION" -p:AgentRuntimeIdentifier="$rid" \
        -p:ServerBuildVersion="$VERSION" -p:ServerRuntimeIdentifier="$rid"
    [ -f "$publish_dir/$binary_name" ] || {
        printf '%s\n' "missing published binary: $publish_dir/$binary_name" >&2
        exit 1
    }
    check_linux_binary "$rid" "$publish_dir/$binary_name"
    cp -p "$publish_dir/$binary_name" "$package_dir/$packaged_name"
    # Single file: settings come from environment variables and SQLite is linked into the Server binary.
    if [ "$rid" = win-x64 ] || [ "$rid" = win-arm64 ]; then
        if command -v zip >/dev/null 2>&1; then
            (cd "$package_dir" && zip -q -X "$OUTPUT_DIR/$archive_name.zip" ./*)
        else
            "$PYTHON" - "$package_dir/$packaged_name" "$OUTPUT_DIR/$archive_name.zip" <<'PY'
import os, sys, zipfile
source, target = sys.argv[1:]
with zipfile.ZipFile(target, "w", zipfile.ZIP_DEFLATED) as archive:
    archive.write(source, os.path.basename(source))
PY
        fi
    else
        tar -C "$package_dir" -czf "$OUTPUT_DIR/$archive_name.tar.gz" .
    fi
}

rm -rf "$REPO_ROOT/.release-publish" "$REPO_ROOT/.release-package"
RELEASE_TSV="$REPO_ROOT/.release-assets.tsv"
: > "$RELEASE_TSV"
TOOLS_TSV="$REPO_ROOT/.release-tool-assets.tsv"
: > "$TOOLS_TSV"

record_asset() {
    rid=$1
    archive=$2
    sha256=$(sha256_of "$OUTPUT_DIR/$archive")
    size=$(wc -c < "$OUTPUT_DIR/$archive" | tr -d ' ')
    printf '%s\t%s\t%s\t%s\n' "$rid" "$archive" "$sha256" "$size" >> "$RELEASE_TSV"
}

publish_and_package_record() {
    publish_and_package "$@"
    rid=$3
    case "$rid" in win-*) ext=zip ;; *) ext=tar.gz ;; esac
    if [ "$2" = agent ]; then prefix=hypanel-agent; else prefix=hypanel-server; fi
    record_asset "$rid" "$prefix-$VERSION-$rid.$ext"
}

for rid in $AGENT_RIDS; do publish_and_package_record src/HyPanel.Agent/HyPanel.Agent.csproj agent "$rid"; done
for rid in $SERVER_RIDS; do publish_and_package src/HyPanel.Server/HyPanel.Server.csproj server "$rid"; done

if [ -f scripts/install.sh ]; then
    cp -p scripts/install.sh "$OUTPUT_DIR/"
else
    printf '%s\n' 'missing scripts/install.sh' >&2
    exit 1
fi
if [ -f scripts/install.ps1 ]; then
    cp -p scripts/install.ps1 "$OUTPUT_DIR/"
else
    printf '%s\n' 'missing scripts/install.ps1' >&2
    exit 1
fi

"$PYTHON" - "$OUTPUT_DIR" "$VERSION" "$PUBLISHED_AT" "$RELEASE_TSV" "${REQUIRE_ALL_AGENT_RIDS:-true}" "$TOOLS_TSV" <<'PY'
import json, os, sys
out, version, published_at, tsv, require_all, tools_tsv = sys.argv[1:]
frozen = {"win-x64", "win-arm64", "osx-x64", "osx-arm64", "linux-x64", "linux-arm64", "linux-musl-x64", "linux-musl-arm64"}
assets = []
seen = set()
with open(tsv, encoding="utf-8") as source:
    for line in source:
        rid, name, sha256, size = line.rstrip("\n").split("\t")
        if rid in seen or rid not in frozen or len(sha256) != 64 or sha256.lower() != sha256:
            raise SystemExit("duplicate or invalid Agent asset: " + rid)
        if not os.path.isfile(os.path.join(out, name)):
            raise SystemExit("missing Agent archive: " + name)
        seen.add(rid)
        assets.append({"rid": rid, "fileName": name, "sha256": sha256, "size": int(size)})
if not assets or (require_all == "true" and seen != frozen):
    raise SystemExit("Agent manifest must contain all 8 frozen RIDs")
assets.sort(key=lambda item: item["rid"])
tools = []
with open(tools_tsv, encoding="utf-8") as source:
    for line in source:
        rid, name, sha256, size = line.rstrip("\n").split("\t")
        tools.append({"backendType":"wireguard-tools", "version":"1.0.20260223", "rid":rid,
            "fileName":name, "sha256":sha256, "size":int(size), "requiresAvx":False})
with open(os.path.join(out, "manifest.json"), "w", encoding="utf-8") as target:
    json.dump({"schemaVersion": 1, "version": version, "publishedAt": published_at, "assets": assets, "tools":tools}, target, separators=(",", ":"))
PY

(
    cd "$OUTPUT_DIR"
    find . -maxdepth 1 -type f \( -name '*.tar.gz' -o -name '*.tar.xz' -o -name '*.zip' -o -name 'wg-*' -o -name 'manifest.json' -o -name 'install.sh' -o -name 'install.ps1' \) -print \
        | sort | while IFS= read -r file; do printf '%s  %s\n' "$(sha256_of "${file#./}")" "${file#./}"; done
) > "$OUTPUT_DIR/SHA256SUMS"

rm -rf "$REPO_ROOT/.release-publish" "$REPO_ROOT/.release-package" "$RELEASE_TSV" "$TOOLS_TSV"
