# HyPanel Context

## Product

HyPanel is a lightweight centralized proxy backend orchestration panel.

It manages remote Agents, not remote shells. An Agent may run multiple independent proxy service instances on one machine.

Primary backend targets:

1. Hysteria2
2. Xray
3. Mihomo
4. sing-box

Backends are adapters behind a compile-time provider interface; HyPanel must not become tied to one proxy core.

## User experience

### Administrator

An administrator can:

- Add a Node.
- Generate a short-lived one-click Agent installer.
- See Node online state, platform, Agent version, CPU/RAM/disk/network basics.
- Add multiple `ServiceInstance` objects to a Node.
- Install/update backend binaries.
- Configure/deploy/start/stop/restart services.
- Update one Agent or a batch of Agents.
- Create users and grant them selected service instances.
- View usage by user/node/service/backend.
- Get a user's independent subscription URL.

### Normal user

A normal user sees only:

- Their subscription URL.
- QR code.
- Usage and traffic limit.
- Expiration.
- Output formats such as Mihomo, sing-box, raw URI, Base64.

They do not see Node, Agent, backend config, logs, or other users.

## One-click Agent install

Admin creates an enrollment link, valid for 15 minutes and one successful use.

Examples:

```bash
curl -fsSL https://panel.example/i/abc123 | sh
```

```powershell
irm https://panel.example/i/abc123 | iex
```

Installer detects OS/architecture/libc, downloads the correct signed or hashed artifact, installs the Agent, registers it, configures startup, and starts it.

Target service managers:

- Linux systemd.
- Alpine OpenRC.
- OpenWrt procd.
- macOS LaunchDaemon.
- Windows Service.

First supported CPU architectures: x64 and arm64. OpenWrt MIPS is intentionally out of scope initially.

## Builds

GitHub Actions should build NativeAOT artifacts for:

Agent:
- win-x64
- win-arm64
- osx-x64
- osx-arm64
- linux-x64
- linux-arm64
- linux-musl-x64
- linux-musl-arm64

Server at minimum:
- linux-x64
- linux-arm64
- linux-musl-x64
- linux-musl-arm64

GHCR:
- linux/amd64
- linux/arm64
- musl/Alpine-based Server image.

Release metadata includes `manifest.json` and SHA256 hashes.

## UI

Visual direction:

- Minimal black/white/gray.
- Rounded controls/cards.
- No decorative gradients or glassmorphism.
- Light / Dark / System three-state theme.
- Responsive but desktop administration is the main target.

## Non-goals for early versions

- Commercial billing.
- Multi-tenant organizations.
- Complex RBAC.
- Backend load balancing as a core feature.
- Arbitrary remote shell.
- Kubernetes/microservice architecture.
- Runtime binary plugin ecosystem.
