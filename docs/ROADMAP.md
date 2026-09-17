# HyPanel Roadmap

The roadmap is ordered to prove the core control plane before adding many proxy backends.

## Phase 0 — Foundation

Deliver:

- Solution/repository structure.
- `HyPanel.Server`, `HyPanel.Agent`, `HyPanel.Shared`.
- Web scaffold.
- .NET 10 NativeAOT baseline.
- Minimal CI.
- Project docs and task tracking.

Exit criteria:

- Normal build succeeds.
- Representative NativeAOT publish succeeds.
- Server and Agent can start.
- Web build succeeds.

## Phase 1 — Agent control plane

Deliver:

- SQLite persistence.
- Basic Admin authentication bootstrap.
- Node model.
- EnrollmentToken issuance.
- One-time Agent enrollment.
- Agent authentication.
- `/api/agent/v1/sync`.
- Online/offline/last-seen.
- Basic Node metrics.
- DesiredRevision / AppliedRevision.
- AgentCommand model and one safe command path.

Exit criteria:

- A fresh Agent can enroll.
- Agent reconnects using permanent credentials.
- Server observes it online/offline.
- A revision/command round trip works.
- Retry behavior does not duplicate commands.

## Phase 2 — Cross-platform Agent installation

Deliver:

- Release manifest consumption.
- `install.sh`.
- `install.ps1`.
- Linux architecture/libc detection.
- systemd service.
- macOS LaunchDaemon.
- Windows Service.
- Alpine OpenRC.
- OpenWrt procd.
- One-click 15-minute, one-use installer route.

Exit criteria:

- Automated install works on representative Windows, macOS, Debian/Ubuntu, Alpine.
- OpenWrt x64/arm64 installation path exists and is testable.

## Phase 3 — First backend: Hysteria2

Deliver:

- Minimal `IBackendProvider` contract.
- Backend binary manager.
- Hysteria2 provider.
- Multiple `ServiceInstance` support.
- Config validation/application.
- Start/stop/restart/status.
- Usage collection where backend capability allows.

Exit criteria:

- Two independent Hysteria2 instances can coexist on one Agent when ports permit.
- Desired state can create/change/remove an instance safely.
- Invalid config does not destroy the previous working config.

## Phase 4 — Users and subscriptions

Deliver:

- Admin/User roles.
- User CRUD.
- UserServiceBinding.
- Traffic limit/expiration model.
- Per-user independent subscription token.
- Initial subscription formats: raw/Base64/Mihomo; sing-box if straightforward.
- User self-service page.
- Usage aggregation and display.

Exit criteria:

- Two users can receive different sets of services from the same panel.
- Usage remains separated per user.
- Normal user cannot read Admin/Node data.

## Phase 5 — Xray

Deliver:

- Xray provider.
- VLESS.
- REALITY.
- Vision.
- VMess where useful.
- Xray usage/stat integration.
- Backend capability reporting.

Exit criteria:

- Hysteria2 and Xray services can coexist on one Agent.
- A user subscription can include entries from both.

## Phase 6 — More backends

Deliver:

- Mihomo provider.
- sing-box provider.
- Only features that map cleanly to HyPanel's existing lifecycle/capability model.

Do not redesign the whole system to expose every obscure backend option.

## Phase 7 — Operational convenience

Deliver as demand justifies:

- Service templates.
- Batch desired-state deployment.
- Batch Agent updates. (completed by OP-005)
- Backend update policies/channels.
- Better logs/health views.
- Optional update signing.
- Optional stronger Agent identity such as mTLS/device keys.

## Explicitly deferred

Until real need appears:

- Organizations/multi-tenancy.
- Complex RBAC.
- Payment/billing.
- Automatic backend load balancing.
- Arbitrary remote shell.
- Redis/message queues.
- Runtime plugin marketplace.

## Phase 11 — Per-user proxy identity

Deliver:

- A distinct recoverable proxy credential for every supported User and Service binding.
- Authenticated encryption under an explicitly configured Server master key.
- Xray VLESS multi-user desired state and runtime configuration.
- Xray API-based cumulative per-user counters with restart-safe delta accounting.
- Credential create, revoke and rotate lifecycle.
- Subscription projection from the authenticated user's credential rather than shared service config.
- Desired-state removal of disabled, expired and traffic-limited users without stopping the service.
- Truthful `MultiUser`, `PerUserTraffic` and aggregate `TrafficStats` capabilities.

Exit criteria:

- Two users bound to one Xray service receive different UUIDs and can connect concurrently.
- Their traffic is accounted independently without retry or restart double counting.
- Disable, expiry, limit and rotation remove only the affected user's old access.
- Server restart preserves decryptable credentials; missing key fails explicitly when encrypted credentials exist.

## Phase 12 — Backend release management

Deliver:

- Fixed official GitHub release sources for Hysteria2, Xray, Mihomo and sing-box.
- Periodic metadata refresh with cache fallback and exact RID asset mapping.
- On-demand bounded download plus safe raw/gzip/zip/tar.gz executable extraction.
- Panel-origin raw artifact delivery with computed size and SHA256.
- Separate Current, Latest and Desired backend versions.
- Manual-by-default and optional per-service automatic updates.
- Per-service post-start health verification and complete desired-version rollback reporting.

Exit criteria:

- A real backend updates through Server cache and Agent reconciliation without direct upstream Agent access.
- Failure after process replacement restores the prior binary/config and Server desired version.
- Other service instances remain unaffected.

## Phase 13 — GHCR container publication

Deliver:

- Multi-platform `ghcr.io/greepar/hypanel` for linux/amd64 and linux/arm64.
- NativeAOT musl Server binaries with no .NET runtime, SDK or compiler in the final image.
- Non-root runtime, `/data` persistence, port 8080 and image HEALTHCHECK.
- Release-version, `latest` and `sha-*` tags from the release workflow.
- No Docker socket and no in-container self-replacement.

## Phase 14 — Server self update

Deliver:

- Independent Server version/RID stamping and update state.
- Bare-metal GitHub discovery, size/SHA verification, safe extraction and exact self-test.
- Atomic executable backup/replacement, same-process exec and startup/stability verification.
- Automatic rollback when the replacement cannot complete verification.
- Docker deployment detection with image pull/recreate guidance only.
- Global Settings page with current/latest/deployment/status information.

Exit criteria:

- Bare-metal Server updates itself without SSH replacement or Docker access.
- Database and release caches survive unchanged.
- Docker mode cannot trigger executable replacement.

## Phase 15 — Global settings

Deliver:

- Top-level Settings navigation.
- Manual/Auto defaults for newly created Agents and Backend services.
- Optional validated HTTPS GitHub mirror base.
- Agent, Backend and Server release-source status.
- Server deployment/update status and SQLite/data-directory information.

The page remains intentionally small; it is not an advanced-settings dump.
