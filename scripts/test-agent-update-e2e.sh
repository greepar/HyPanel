#!/bin/sh
set -eu

REPO_ROOT=$(CDPATH='' cd -- "$(dirname -- "$0")/.." && pwd)
OLD_VERSION=${OLD_VERSION:-1.2.0}
NEW_VERSION=${NEW_VERSION:-1.3.0}
case "$(uname -s)-$(uname -m)" in
    Linux-x86_64) RID=linux-x64; EXT=tar.gz ;;
    Linux-aarch64|Linux-arm64) RID=linux-arm64; EXT=tar.gz ;;
    Darwin-x86_64) RID=osx-x64; EXT=tar.gz ;;
    Darwin-arm64) RID=osx-arm64; EXT=tar.gz ;;
    *) printf '%s\n' 'unsupported E2E host' >&2; exit 2 ;;
esac

ROOT=$(mktemp -d "${TMPDIR:-/tmp}/hypanel-update-e2e.XXXXXX")
cleanup() {
    [ -n "${AGENT_PID:-}" ] && kill "$AGENT_PID" >/dev/null 2>&1 || true
    [ -n "${PANEL_PID:-}" ] && kill "$PANEL_PID" >/dev/null 2>&1 || true
    if [ "${KEEP_E2E_ROOT:-0}" = 1 ]; then printf '%s\n' "E2E root retained: $ROOT" >&2; else rm -rf "$ROOT"; fi
}
trap cleanup EXIT HUP INT TERM
mkdir -p "$ROOT/old" "$ROOT/new" "$ROOT/install" "$ROOT/data"

publish_agent() {
    version=$1
    output=$2
    dotnet publish "$REPO_ROOT/src/HyPanel.Agent/HyPanel.Agent.csproj" -c Release -r "$RID" \
        --self-contained true -o "$output" -p:Version="$version" -p:VersionPrefix="$version" \
        -p:FileVersion="$version.0" -p:InformationalVersion="$version" \
        -p:IncludeSourceRevisionInInformationalVersion=false \
        -p:AgentBuildVersion="$version" -p:AgentRuntimeIdentifier="$RID" >/dev/null
}
publish_agent "$OLD_VERSION" "$ROOT/old"
publish_agent "$NEW_VERSION" "$ROOT/new"
cp "$ROOT/old/HyPanel.Agent" "$ROOT/install/HyPanel.Agent"
chmod 755 "$ROOT/install/HyPanel.Agent"
ASSET="hypanel-agent-$NEW_VERSION-$RID.$EXT"
tar -C "$ROOT/new" -czf "$ROOT/$ASSET" HyPanel.Agent
SIZE=$(wc -c < "$ROOT/$ASSET" | tr -d ' ')
if command -v sha256sum >/dev/null 2>&1; then SHA=$(sha256sum "$ROOT/$ASSET" | cut -d ' ' -f 1)
else SHA=$(shasum -a 256 "$ROOT/$ASSET" | cut -d ' ' -f 1); fi

cat > "$ROOT/panel.py" <<'PY'
import http.server, json, os, pathlib, sys
root, asset, version, rid, sha, size = sys.argv[1:]
marker = pathlib.Path(root, "verified")
class Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *_): pass
    def do_GET(self):
        if self.path != "/api/releases/v1/assets/" + asset:
            self.send_error(404); return
        data = pathlib.Path(root, asset).read_bytes()
        self.send_response(200); self.send_header("Content-Length", str(len(data))); self.end_headers(); self.wfile.write(data)
    def do_POST(self):
        if self.path != "/api/agent/v1/sync": self.send_error(404); return
        length = int(self.headers.get("Content-Length", "0")); request = json.loads(self.rfile.read(length))
        report = request.get("agentUpdate")
        current = request["agentVersion"]
        if current == version and report and report["status"] == "Verifying": marker.write_text("verified", encoding="utf-8")
        offer = None if current == version else {"updateId":"11111111-1111-1111-1111-111111111111","version":version,"rid":rid,"fileName":asset,"sha256":sha,"size":int(size)}
        body = json.dumps({"desiredRevision":0,"desiredState":None,"commands":[],"acceptedUsageBatchIds":[],"syncIntervalSeconds":1,"agentUpdate":offer}, separators=(",", ":")).encode()
        self.send_response(200); self.send_header("Content-Type", "application/json"); self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
pathlib.Path(root, "port").write_text(str(server.server_port), encoding="ascii")
server.serve_forever()
PY
python3 "$ROOT/panel.py" "$ROOT" "$ASSET" "$NEW_VERSION" "$RID" "$SHA" "$SIZE" &
PANEL_PID=$!
while [ ! -s "$ROOT/port" ]; do sleep 0.1; done
PORT=$(cat "$ROOT/port")
cat > "$ROOT/data/credentials.json" <<EOF
{"panelBaseUrl":"http://127.0.0.1:$PORT","agentId":"22222222-2222-2222-2222-222222222222","agentSecret":"e2e-secret","nodeId":"33333333-3333-3333-3333-333333333333","syncIntervalSeconds":1}
EOF
cp "$ROOT/data/credentials.json" "$ROOT/credentials.before"
HYPANEL_DATA_DIR="$ROOT/data" "$ROOT/install/HyPanel.Agent" >"$ROOT/agent.log" 2>&1 &
AGENT_PID=$!

elapsed=0
while [ "$elapsed" -lt 120 ] && [ ! -f "$ROOT/verified" ]; do
    kill -0 "$AGENT_PID" 2>/dev/null || { cat "$ROOT/agent.log" >&2; exit 1; }
    sleep 1; elapsed=$((elapsed + 1))
done
[ -f "$ROOT/verified" ] || { cat "$ROOT/agent.log" >&2; printf '%s\n' 'new Agent did not verify sync' >&2; exit 1; }
elapsed=0
while [ "$elapsed" -lt 10 ] && ! grep -q '"status":"Succeeded"' "$ROOT/data/agent-update-state.json"; do sleep 1; elapsed=$((elapsed + 1)); done
cmp "$ROOT/credentials.before" "$ROOT/data/credentials.json"
[ ! -e "$ROOT/install/HyPanel.Agent.previous" ]
SELF_TEST=$($ROOT/install/HyPanel.Agent --self-test)
[ "$SELF_TEST" = "$(printf '%s\t%s' "$NEW_VERSION" "$RID")" ]
grep -q '"status":"Succeeded"' "$ROOT/data/agent-update-state.json"
printf '%s\n' "Agent update E2E passed: $OLD_VERSION -> $NEW_VERSION ($RID), credentials preserved, sync verified"
