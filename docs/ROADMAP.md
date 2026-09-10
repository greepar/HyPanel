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
- Batch Agent updates.
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
