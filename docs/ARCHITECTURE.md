# HyPanel Architecture

## 1. Topology

```text
                    HyPanel.Server
                ASP.NET Core NativeAOT
                         |
                 HTTPS Agent Sync
                         |
          +--------------+--------------+
          |              |              |
      Agent HK       Agent JP       Agent US
          |              |              |
      services[]      services[]      services[]
       /   |   \          |             /   \
     HY2 Xray Mihomo   sing-box       HY2  Xray
```

Agents initiate control-plane traffic. No inbound Agent management port is required.

## 2. Components

### HyPanel.Server

Responsibilities:

- Admin/user authentication and authorization.
- Persistent data.
- Enrollment token issuance.
- Agent registration/authentication.
- Agent sync API.
- Desired state and revision management.
- One-shot Agent command queue/state.
- User/service binding.
- Usage aggregation.
- Subscription generation.
- Release/update metadata.
- Static web UI hosting.

Keep it a single process for the initial architecture.

### HyPanel.Agent

Responsibilities:

- Persist Agent identity.
- Periodically sync with Server.
- Report Node health and service state.
- Reconcile actual services against desired state.
- Manage backend binaries.
- Manage backend processes/instances.
- Apply validated configs atomically.
- Collect normalized usage.
- Execute approved one-shot commands.
- Self-update safely.

Agent keeps operating existing proxy services when the panel is temporarily unreachable.

### HyPanel.Shared

Contains only truly shared public contracts and small primitives:

- Sync request/response DTOs.
- Desired state DTOs.
- Command DTOs.
- Usage envelope/records.
- Backend type/capabilities.
- Common API error/result contracts when genuinely shared.
- JSON source-generation context as appropriate.

Do not turn Shared into a dumping ground.

### HyPanel.Web

Lightweight SPA/static web client.

Recommended initial stack:

- TypeScript.
- Preact.
- Vite.

The Server build runs the Web production build and embeds the generated Vite
assets into the managed assembly and NativeAOT executable. Deployment remains
a single immutable Server binary; there is no mutable external Web root. The
runtime exposes only `/` and `/assets/{file}` for embedded content and does not
use an SPA catch-all that could turn an unknown `/api/*` route into HTML 200.

## 3. Core domain

### Node

Represents one managed machine.

A Node has zero or many ServiceInstances.

### ServiceInstance

Represents one independently managed proxy service configuration/process.

Examples on one Node:

```text
Service A: Xray / VLESS Reality Vision / TCP 443
Service B: Hysteria2 / UDP 443
Service C: Xray / VMess / TCP 8443
Service D: Mihomo / another listener set
```

TCP/443 and UDP/443 do not conflict.

### BackendProvider

Compile-time adapter from HyPanel's generic lifecycle into backend-specific behavior.

Frozen Phase 3 shape lives in `HyPanel.Agent.Backends.IBackendProvider`:

```csharp
interface IBackendProvider
{
    string BackendType { get; }
    BackendCapabilities Capabilities { get; }

    ValueTask<BackendValidationResult> ValidateAsync(...);
    ValueTask<RenderedBackendConfig> RenderConfigAsync(...);
    ValueTask<BackendProcessSpec> CreateProcessSpecAsync(...);
    ValueTask<BackendHealthResult> CheckHealthAsync(...);
    ValueTask<BackendTrafficSnapshot?> CollectTrafficAsync(...);
}
```

Providers do not download binaries, write live files, or own generic process
supervision. Shared Agent infrastructure performs hash-verified same-origin
download, staging/atomic replacement, process lifetime, log boundaries, and
rollback. This keeps security-critical mechanics consistent across backends.

The first Xray provider is intentionally limited to VLESS/TCP/REALITY/Vision.
Additional transports and protocols require a new schema version instead of
catch-all backend JSON. The provider owns strict parsing, deterministic Xray
JSON rendering, TCP port declaration, process specification and local health.
Shared infrastructure continues to own artifact acquisition, files, process
lifetime and rollback. The REALITY private key stays in operational desired
config and is always redacted; subscriptions receive only the public key.
Phase 5 does not advertise Xray traffic statistics or hot reload.

Mihomo and sing-box v1 intentionally share one Shadowsocks 2022 schema. This
tests the provider boundary without turning either core into arbitrary config
passthrough. Providers own deterministic native YAML/JSON rendering and declare
the same listen port for TCP and UDP; shared reconciliation retains acquisition,
atomic replacement, supervision and rollback ownership.

Potential capabilities:

- Users
- TrafficStats
- HotReload
- Logs
- VersionQuery
- MultiInbound
- ConfigValidation

### DesiredState

Server defines what should exist, not a sequence of shell commands.

Desired state contains a monotonically increasing revision and the services expected on a Node.

Agent performs reconciliation:

```text
desired missing locally     -> install/create/start
desired config changed      -> validate/apply/reload
desired disabled            -> stop
local managed service gone  -> restore
service removed from desired -> stop/remove managed instance
```

Agent reports the last fully applied revision.

### AgentCommand

For transient actions that are not durable desired state:

- StartService
- StopService
- RestartService
- UpdateAgent
- UpdateBackend
- CollectLogs
- RunHealthCheck

Each command has a stable ID and execution status:

- Pending
- Running
- Succeeded
- Failed

Commands must be idempotent or safely de-duplicated by command ID.

## 4. Sync protocol

Initial control plane: HTTPS polling every roughly 5-10 seconds.

Conceptual request:

```json
{
  "agentVersion": "0.1.0",
  "platform": "linux-musl-arm64",
  "appliedRevision": 12,
  "services": [],
  "usage": [],
  "commandResults": []
}
```

Conceptual response:

```json
{
  "desiredRevision": 13,
  "desiredState": {},
  "commands": [],
  "agentUpdate": null
}
```

Exact schema belongs in `docs/CONTRACTS.md` and `HyPanel.Shared`.

Do not introduce WebSocket/SignalR until polling produces a concrete limitation.

## 5. Persistence

Initial Server DB: SQLite.

Keep tables aligned to product concepts rather than proxy protocols.

Expected initial entities:

- Users
- Nodes
- ServiceInstances
- UserServiceBindings
- AgentCommands
- Usage
- EnrollmentTokens
- Release/update metadata if persisted
- ServiceTemplates

Protocol-specific service configuration may be stored as versioned JSON rather than creating many protocol tables.

Secrets must not be accidentally exposed through generic DTO serialization.

### Phase 7 operational persistence

Migration v6 adds `service_templates`. Template display names are NFKC-trimmed
and have an invariant-lowercase unique key. Backend/version/schema and config
use the same validation boundary as service creation, and Admin reads redact
backend secrets.

Template instantiation creates an enabled ServiceInstance and increments its
Node revision in one transaction. Batch service enable/disable first validates
every Node/service ownership pair, then changes all requested desired state in
one transaction and increments each affected Node revision exactly once. Any
invalid item rolls back the complete batch.

The health summary is derived from current Node observations and service
runtime rows. It is a snapshot, not a second monitoring persistence model.
Batch Agent binary update, backend update channels, update signing, and mTLS or
device keys remain deferred until a dedicated artifact trust, handoff,
verification, and rollback protocol is frozen.

### Phase 1 SQLite schema

Phase 1 uses `Microsoft.Data.Sqlite` with explicit parameterized SQL and a
small startup migration runner. EF Core is intentionally not introduced.
SQLite foreign keys and WAL mode are enabled for every opened connection.

The initial tables are:

- `schema_migrations`: applied migration version and timestamp.
- `nodes`: Node identity, display name, desired revision, and creation time.
- `agents`: Agent identity, unique Node binding, SHA-256 secret hash,
  enrollment/last-seen timestamps, reported version/platform, applied revision,
  and latest validated metric snapshot.
- `enrollment_tokens`: token identity, unique SHA-256 token hash, target Node,
  creation/expiration/consumption timestamps, and the consuming Agent ID.
- `agent_commands`: stable command ID, target Agent, type, lifecycle status,
  timestamps, and safe error fields.

GUIDs and UTC timestamps use canonical text representations. Revisions and
byte counters are non-negative SQLite integers. Token consumption and Agent
creation are one transaction. A unique Agent/command ID plus terminal-state
updates make command result ingestion retry-safe.

The basic Admin bootstrap credential is supplied through the
`HYPANEL_ADMIN_TOKEN` environment/configuration secret. It is compared in
constant time and is never persisted or logged. This is an initial single-admin
bootstrap boundary, not the Phase 4 user account model.

## 6. Agent storage

Representative Unix layout:

```text
/var/lib/hypanel/
├── agent.json
├── state.json
├── bin/
│   ├── hysteria/<version>/
│   ├── xray/<version>/
│   ├── mihomo/<version>/
│   └── sing-box/<version>/
├── services/
│   └── <service-id>/
│       ├── config.*
│       ├── state.json
│       └── logs/
└── update/
```

A backend binary version can be shared by multiple ServiceInstances.

Use platform-appropriate locations on Windows/macOS.

## 7. Enrollment and Agent auth

Enrollment URL/token:

- Cryptographically random.
- Bound to a target Node/pending enrollment.
- 15-minute expiration.
- One successful use.
- Atomically consumed to prevent replay.

After enrollment, Agent receives/stores:

- Agent ID.
- High-entropy Agent secret.
- Panel base URL.

Initial transport security is HTTPS + Agent credential. Future mTLS/device keys may be added without being required in v0.1.

Protect Agent credential at rest using restrictive file permissions on Unix and appropriate Windows protection when practical.

## 8. Updating

### Agent

Update flow:

1. Server advertises desired Agent release.
2. Agent downloads to staging.
3. Verify SHA256; later support a cryptographic release signature.
4. Launch/update through a replacement mode/helper copy.
5. Replace executable atomically where possible.
6. Restart service.
7. Report running version.

Windows must account for locked executable replacement.

### Server

Bare-metal Server can use a similar staged self-update design.

Docker deployment must not mount the Docker socket into HyPanel merely to self-update. The panel may advertise that an image update exists; operators update the container externally.

## 9. Usage accounting

Providers normalize backend-specific counters into a shared usage representation.

Required views:

- Per User.
- Per Node.
- Per ServiceInstance.
- Per Backend.
- Total.

Retry-safe ingestion is mandatory. Prefer a simple explicit idempotency key/window/sequence scheme over complicated event infrastructure.

Phase 4 uses transactional delta batches. `(AgentId, BatchId)` is the durable
idempotency key: inserting the receipt and adding every user/service delta occur
in one SQLite transaction. Duplicate receipts are acknowledged without adding
again. Agents persist unacknowledged batches. This handles ambiguous sync
retries without event-stream infrastructure.

## 10. Security boundaries

- Admin APIs require Admin.
- User APIs expose only the current user's resources.
- Agent APIs use Agent authentication, not user auth.
- Enrollment endpoint accepts only valid enrollment tokens.
- Subscription tokens are high entropy and revocable.
- Secrets never appear in normal logs.
- Update artifacts are integrity-checked.
- Remote arbitrary shell is not an ordinary capability.
- Config file paths are generated/validated by HyPanel, never directly trusted from user input.
- Backend child process failures are isolated from the Agent process.

## 11. Simplicity rule

Default answer to a new infrastructure dependency is "no" until a measured need exists.

Start with:

```text
1 Server process
1 SQLite DB
N Agent processes
N backend child processes
HTTPS
```

Scale architecture only when real usage demonstrates a bottleneck.
