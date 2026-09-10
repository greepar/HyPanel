# HyPanel

HyPanel is a lightweight centralized proxy backend orchestration panel built around .NET 10 NativeAOT.

One Server manages many cross-platform Agents. Each Agent can run and manage multiple independent service instances backed by Hysteria2, Xray, Mihomo, sing-box, and future adapters.

## Core idea

```text
Server owns Desired State
          |
          | HTTPS sync
          v
Agent reconciles Actual State
          |
          +-- ServiceInstance: Hysteria2
          +-- ServiceInstance: Xray / VLESS REALITY Vision
          +-- ServiceInstance: Mihomo
          +-- ServiceInstance: sing-box
```

The project intentionally avoids a heavy distributed-control stack. Early versions use one Server process, SQLite, HTTPS Agent polling, and backend child processes.

See:

- `docs/CONTEXT.md` — compact product context.
- `docs/ARCHITECTURE.md` — architecture.
- `docs/CONTRACTS.md` — public contract invariants.
- `docs/ROADMAP.md` — phased delivery plan.
- `TASKS.md` — current work queue.

## Local verification

The solution includes focused Shared contract, Server persistence, and Agent
state/metrics tests.

```bash
dotnet restore HyPanel.slnx
dotnet build HyPanel.slnx --configuration Release --no-restore
dotnet test HyPanel.slnx --configuration Release --no-restore
```

Publish representative Linux x64 NativeAOT binaries:

```bash
dotnet publish src/HyPanel.Server/HyPanel.Server.csproj \
  --configuration Release --runtime linux-x64 --self-contained true
dotnet publish src/HyPanel.Agent/HyPanel.Agent.csproj \
  --configuration Release --runtime linux-x64 --self-contained true
```

Build all supported release archives and checksums:

```bash
SOURCE_DATE_EPOCH=$(date +%s) sh scripts/build-release.sh 0.2.0 artifacts/release
```

The Server serves the manifest, verified same-origin assets, and fixed
installers from `HyPanel:ReleasesDirectory`. Generate short-lived installation
commands through the Admin API; enrollment tokens are never placed in URLs and
are removed from service configuration after successful enrollment.

Build the web application:

```bash
cd web/HyPanel.Web
npm ci
npm run build
```

## License

HyPanel is licensed under the GNU General Public License v3.0 only
(`GPL-3.0-only`). See `LICENSE` for the complete license text.
