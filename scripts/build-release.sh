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

publish_and_package() {
    project=$1
    kind=$2
    rid=$3
    publish_dir="$REPO_ROOT/.release-publish/$kind-$rid"
    package_dir="$REPO_ROOT/.release-package/$kind-$rid"
    project_name=$(basename "$project" .csproj)
    binary_name=$project_name
    case "$rid" in win-*) binary_name="$binary_name.exe" ;; esac
    if [ "$kind" = agent ]; then
        archive_name="hypanel-agent-$VERSION-$rid"
    else
        archive_name="hypanel-server-$VERSION-$rid"
    fi

    rm -rf "$publish_dir" "$package_dir"
    mkdir -p "$package_dir"
    dotnet publish "$project" -c Release -r "$rid" --self-contained true -o "$publish_dir"
    [ -f "$publish_dir/$binary_name" ] || {
        printf '%s\n' "missing published binary: $publish_dir/$binary_name" >&2
        exit 1
    }
    cp -p "$publish_dir/$binary_name" "$package_dir/"
    if [ -f "$publish_dir/appsettings.json" ]; then
        cp -p "$publish_dir/appsettings.json" "$package_dir/"
    fi
    if [ "$kind" = server ] && [ -f "$publish_dir/libe_sqlite3.so" ]; then
        cp -p "$publish_dir/libe_sqlite3.so" "$package_dir/"
    fi
    if [ "$rid" = win-x64 ] || [ "$rid" = win-arm64 ]; then
        command -v zip >/dev/null 2>&1 || { printf '%s\n' 'zip is required for Windows artifacts' >&2; exit 1; }
        (cd "$package_dir" && zip -q -X "$OUTPUT_DIR/$archive_name.zip" ./*)
    else
        tar -C "$package_dir" -czf "$OUTPUT_DIR/$archive_name.tar.gz" .
    fi
}

rm -rf "$REPO_ROOT/.release-publish" "$REPO_ROOT/.release-package"
RELEASE_TSV="$REPO_ROOT/.release-assets.tsv"
: > "$RELEASE_TSV"

record_asset() {
    rid=$1
    archive=$2
    sha256=$(sha256sum "$OUTPUT_DIR/$archive" | cut -d ' ' -f 1)
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

python3 - "$OUTPUT_DIR" "$VERSION" "$PUBLISHED_AT" "$RELEASE_TSV" "${REQUIRE_ALL_AGENT_RIDS:-true}" <<'PY'
import json, os, sys
out, version, published_at, tsv, require_all = sys.argv[1:]
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
with open(os.path.join(out, "manifest.json"), "w", encoding="utf-8") as target:
    json.dump({"schemaVersion": 1, "version": version, "publishedAt": published_at, "assets": assets}, target, separators=(",", ":"))
PY

(
    cd "$OUTPUT_DIR"
    find . -maxdepth 1 -type f \( -name '*.tar.gz' -o -name '*.zip' -o -name 'manifest.json' -o -name 'install.sh' -o -name 'install.ps1' \) -print \
        | sort | while IFS= read -r file; do sha256sum "${file#./}"; done
) > "$OUTPUT_DIR/SHA256SUMS"

rm -rf "$REPO_ROOT/.release-publish" "$REPO_ROOT/.release-package" "$RELEASE_TSV"
