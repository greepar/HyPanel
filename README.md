# HyPanel

Lightweight centralized proxy backend orchestration panel based on .NET 10 NativeAOT.

HyPanel runs one control-plane Server and outbound-polling Agents on your nodes. Each Agent can reconcile multiple
independent proxy services without exposing a management port or accepting arbitrary remote shell commands.

## Features

- One Server manages many Nodes and multiple Services per Node.
- Hysteria2, Xray, Mihomo and sing-box compile-time Backend providers.
- Short-lived one-click Agent enrollment with durable high-entropy Agent credentials.
- Desired-state reconciliation, per-Service health, logs and isolated failure recovery.
- Agent, Backend and bare-metal Server updates with size/SHA256 verification and rollback.
- Users, per-Service grants, subscription URLs and Xray per-user traffic accounting.
- Panel-managed Hysteria2 certificates with encrypted private keys and safe rotation.
- WAL-safe online backups, restore preflight, emergency rollback and disaster-recovery CLI.
- SQLite, NativeAOT and a lightweight Preact/Vite administration UI.

## Architecture

```text
                         HTTPS
 Browser/Admin  <-------------------->  HyPanel.Server
                                             |
                                             | desired state + commands
                                             | outbound Agent polling
                                             v
                                      HyPanel.Agent
                                      |    |    |
                                      HY2  Xray  other Services
```

The Server owns desired state in SQLite. Agents reconcile actual Backend processes toward that state. Nodes do not
need an inbound Agent management port, and HyPanel does not use Redis, a message queue, runtime plugins or a persistent
SSH control channel. See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the detailed boundaries.

## Quick Start

Docker Compose is the recommended way to start the Server. Generate and safely store both secrets first:

```bash
export HYPANEL_ADMIN_TOKEN="$(openssl rand -hex 32)"
export HYPANEL_MASTER_KEY="$(openssl rand -base64 32)"
```

`HYPANEL_MASTER_KEY` encrypts proxy credentials and certificate private keys. It must remain stable for the lifetime of
the installation.

Create `compose.yml`:

```yaml
services:
  hypanel:
    image: ghcr.io/greepar/hypanel:latest
    restart: unless-stopped
    ports:
      - "8080:8080"
    environment:
      HYPANEL_ADMIN_TOKEN: ${HYPANEL_ADMIN_TOKEN:?required}
      HYPANEL_MASTER_KEY: ${HYPANEL_MASTER_KEY:?required}
    volumes:
      - hypanel-data:/data

volumes:
  hypanel-data:
```

Start it:

```bash
docker compose up -d
```

Open `http://SERVER_IP:8080`, choose **Initial token**, and enter `HYPANEL_ADMIN_TOKEN`. Create an Admin account for
normal use, then place the Server behind an HTTPS reverse proxy before enrolling Internet nodes.

## Docker

The image is published for `linux/amd64` and `linux/arm64`, runs as a non-root user, listens on port 8080 and stores
durable state beneath `/data`. Do not mount the Docker socket into HyPanel.

Docker Server updates are owned by the operator:

```bash
docker compose pull
docker compose up -d
```

HyPanel reports image availability but never replaces its own binary inside a container.

## Bare-metal Server

Download the matching `hypanel-server-<version>-linux-<arch>.tar.gz` from
[GitHub Releases](https://github.com/greepar/HyPanel/releases), verify it against `SHA256SUMS`, and install it at
`/opt/hypanel/server/hypanel-server`. A hardened example unit is provided at
[`deploy/systemd/hypanel-server.service`](deploy/systemd/hypanel-server.service).

Create `/etc/hypanel/server.env` with permissions `0600`:

```bash
HYPANEL_ADMIN_TOKEN=<high-entropy bootstrap token>
HYPANEL_MASTER_KEY=<persistent 32-byte Base64 key>
HYPANEL_DATA_DIR=/var/lib/hypanel
```

The example unit binds `127.0.0.1:5291`; publish it through an HTTPS reverse proxy. The dedicated `hypanel` user must
own `/var/lib/hypanel` and `/opt/hypanel/server` so backup/restore and verified self-update can use their fixed paths.

## Add Agent

1. Sign in as Admin and create a Node.
2. Open the Node and copy its generated Linux install command.
3. Run the command as root on the target node.
4. Wait for the Node to become Online, then create Services from its detail page.

The command resembles:

```bash
curl -fsSL https://panel.example/i/123456 | sh
```

The six-digit code is valid for 15 minutes and one successful enrollment. It is not the Agent's long-term password.
After enrollment, the Agent stores a separate high-entropy identity and polls the Server over HTTPS.

## Create Service

Current shipped profiles are:

| Backend | Profile | Current capability |
| --- | --- | --- |
| Hysteria2 | Hysteria2 / QUIC | Panel-managed certificate selection; one configured auth password |
| Xray | VLESS TCP REALITY Vision | Independent per-user UUIDs and official per-user traffic counters |
| Mihomo | Shadowsocks 2022 | Managed single inbound password; no claimed per-user traffic |
| sing-box | Shadowsocks 2022 | Managed single inbound password; no claimed per-user traffic |

For Hysteria2, upload a matching certificate/private-key pair under **Settings → TLS Certificates**, then select that
managed certificate in the Service form. The normal API never returns the private key.

For Xray, configure the REALITY key pair, short ID, server name and destination. Granting a user access and rotating
their Service credential creates a distinct VLESS UUID used for subscription and traffic accounting.

## Users & Subscription

Create a User, grant only the required Services, and rotate/create credentials where supported. Each User receives a
high-entropy, revocable subscription token. Available projections include raw links, Base64, Mihomo and sing-box.

Normal users see only their own subscription and usage. They cannot inspect Nodes, Agent state, Backend configuration,
diagnostics or other users.

## Updates

- **Agent Update:** the Server offers a verified release; the Agent stages, self-tests, replaces and proves a successful
  authenticated reconnect before deleting its previous binary.
- **Backend Update:** the Server caches official artifacts; the Agent reconciles one target Service and rolls back that
  Service on failed health verification.
- **Server Update:** bare-metal Linux downloads and self-tests the official Server asset, atomically replaces it and
  verifies startup. Docker installations do not use binary self-replacement.
- **Docker Update:** run `docker compose pull && docker compose up -d` externally.

Automatic Agent/Backend updates are opt-in. Manual is the default.

## Backup & Restore

Create and restore backups under **Settings → Data Backup & Restore**. Restore first validates format, SHA256, SQLite
integrity, schema compatibility, repository shape, MasterKey fingerprint and every encrypted credential/key. A
successful request creates an emergency backup and restarts the Server before replacement; failed migration or startup
validation restores the prior database.

The bare-metal CLI remains available if the Web UI cannot start:

```bash
# Online backup is allowed while the Server runs.
sudo -u hypanel /opt/hypanel/server/hypanel-server backup

sudo -u hypanel /opt/hypanel/server/hypanel-server backup validate /path/to/hypanel-backup.tar.gz

# Offline restore refuses to run while the Server process owns the data directory.
sudo systemctl stop hypanel-server
sudo -u hypanel /opt/hypanel/server/hypanel-server restore /path/to/hypanel-backup.tar.gz
sudo systemctl start hypanel-server
```

**A database backup alone is not complete disaster recovery.** Preserve both:

```text
HyPanel backup archive
+
the matching HYPANEL_MASTER_KEY
```

The MasterKey is intentionally not inside the archive. Store it separately from backups and do not place both in the
same unprotected location. Without the matching key, encrypted proxy credentials and certificate private keys cannot
be recovered.

## Security

- Use HTTPS for every Internet-facing Server and Agent connection.
- Keep `HYPANEL_ADMIN_TOKEN` and `HYPANEL_MASTER_KEY` out of source control and logs.
- Treat backup archives as sensitive: they contain users, service config, token hashes and usage, even though dedicated
  credential/private-key fields remain encrypted.
- Run Server and Agent under their dedicated unprivileged system users.
- Expose only required proxy Service ports. The Agent control plane is outbound-only.
- HyPanel intentionally provides no arbitrary remote shell and no Docker socket integration.

## Supported Platforms

Production acceptance currently focuses on Linux glibc x64 with systemd. Linux arm64 NativeAOT artifacts and CI gates
are published, but real arm64 glibc runtime acceptance is still an explicit evidence gap. Release assets also exist for
other RIDs; see [`docs/CROSS_PLATFORM_ACCEPTANCE.md`](docs/CROSS_PLATFORM_ACCEPTANCE.md) before treating them as
production-verified. OpenWrt MIPS is not currently supported.

## Troubleshooting

Useful Linux commands:

```bash
systemctl status hypanel-agent --no-pager
journalctl -u hypanel-agent -n 100 --no-pager
systemctl restart hypanel-agent

systemctl status hypanel-server --no-pager
journalctl -u hypanel-server -n 100 --no-pager
systemctl restart hypanel-server
```

- **Agent offline:** verify DNS/HTTPS reachability, system time and `hypanel-agent` logs. Running Backends stay up during
  a temporary Panel outage.
- **Enrollment expired:** generate a new Node install command. Enrollment codes are intentionally short-lived and
  single-use.
- **Backend failed:** inspect the Service error and bounded diagnostic logs; check port conflicts and configuration.
- **Update rollback:** inspect Agent or Server update status and journal entries. The previous binary is retained until
  the replacement proves healthy.
- **Certificate expired:** replace the managed certificate in Settings. Only referencing Hysteria2 Services reconcile.
- **MasterKey missing/mismatch:** restore the original persistent `HYPANEL_MASTER_KEY`; do not generate a replacement
  for an existing database.
- **Restore failed:** read `restore-status.json` and Server logs. HyPanel retains the current DB on preflight failure and
  creates an emergency backup before replacement.
- **Subscription empty:** confirm the User is enabled/not expired, has Service grants, and has an active credential for
  profiles that require one.

## Development

Requirements: the SDK pinned by [`global.json`](global.json), Node.js 22+, and a NativeAOT toolchain for publish gates.

```bash
npm ci --prefix web/HyPanel.Web
dotnet build HyPanel.slnx -c Release
dotnet test HyPanel.slnx -c Release --no-build
npm run build --prefix web/HyPanel.Web
dotnet publish src/HyPanel.Server/HyPanel.Server.csproj -c Release -r linux-x64
dotnet publish src/HyPanel.Agent/HyPanel.Agent.csproj -c Release -r linux-x64
```

Contributor architecture and contract rules are in [`AGENTS.md`](AGENTS.md), [`docs/CONTEXT.md`](docs/CONTEXT.md),
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and [`docs/CONTRACTS.md`](docs/CONTRACTS.md).

## License

HyPanel is licensed under the [GNU General Public License v3.0](LICENSE).
