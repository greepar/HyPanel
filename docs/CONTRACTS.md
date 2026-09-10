# HyPanel Contracts

This file defines contract ownership and invariants. Exact C# types should be implemented in `HyPanel.Shared` and kept in sync with this document.

Public contract changes require the primary architect.

## 1. Versioning rules

- Agent sync API starts at `/api/agent/v1/...`.
- Subscription/user/admin APIs should be versioned consistently when public stability matters.
- DTOs should be additive when possible.
- Do not depend on polymorphic reflection-heavy serialization.
- Register NativeAOT JSON DTOs with source generation.
- Reject unknown dangerous state changes rather than silently interpreting them.

## 2. Agent identity

After enrollment, each Agent has:

```text
AgentId
AgentSecret
NodeId
```

`AgentSecret` is high-entropy and never returned through Admin/User read APIs.

Agent requests authenticate independently from normal users.

For v1, authenticated Agent requests send the Agent ID in
`X-HyPanel-Agent-Id` and the high-entropy secret as
`Authorization: Bearer <agent-secret>` over HTTPS. The Server stores only a
one-way hash of the secret and uses a constant-time comparison. Authentication
failures return the same response regardless of whether the ID or secret was
wrong. Agent credentials are never accepted over plain HTTP outside an
explicit local development environment.

## 3. Enrollment

Endpoint:

```text
POST /api/agent/v1/enroll
```

Conceptual request:

```json
{
  "token": "<one-time enrollment token>",
  "agentVersion": "0.1.0",
  "platform": "linux-arm64",
  "machine": {
    "hostname": "hk-01"
  }
}
```

Conceptual success response:

```json
{
  "agentId": "...",
  "agentSecret": "...",
  "nodeId": "...",
  "syncIntervalSeconds": 8
}
```

Invariants:

- Token expires 15 minutes after issuance.
- Token succeeds at most once.
- Consumption and credential issuance are atomic.
- Failed/expired/replayed token gives no sensitive details.
- Enrollment tokens and Agent secrets each contain at least 256 bits of random
  entropy. Only their one-way hashes are persisted by the Server.
- Token lookup, expiry validation, atomic consumption, and Agent credential
  creation occur in one SQLite transaction.
- A successful response is the only time the plaintext Agent secret is
  returned.

## 4. Agent sync

Endpoint concept:

```text
POST /api/agent/v1/sync
```

Request should eventually include:

```text
AgentId/auth context
AgentVersion
Platform
AppliedRevision
NodeMetrics
ServiceRuntimeStates
UsageBatches
CommandResults
```

The Phase 3 request extends the Phase 1 fields with:

```text
AgentVersion: string
Platform: string
AppliedRevision: signed 64-bit non-negative integer
Metrics: NodeMetrics
CommandResults: AgentCommandResult[]
Services: ServiceRuntimeState[]
UsageBatches: UsageBatch[]
```

`NodeMetrics` contains an UTC `ObservedAt`, uptime seconds, CPU percentage,
total/available memory and disk bytes, and cumulative network upload/download
bytes. Numeric counters must be non-negative; CPU is in the inclusive range
0..100. The Server validates bounds rather than trusting Agent input.

Response should eventually include:

```text
DesiredRevision
DesiredState when needed
PendingCommands
AgentUpdate when needed
Optional sync interval/config
```

The Phase 1 response freezes these exact fields:

```text
DesiredRevision: signed 64-bit non-negative integer
DesiredState: NodeDesiredState? (omitted as null when already current)
Commands: AgentCommand[]
SyncIntervalSeconds: integer
AcceptedUsageBatchIds: Guid[]
```

From Phase 3, `NodeDesiredState` contains `Revision`, `Services[]`, and
`BackendArtifacts[]`. An artifact is identified by backend type, exact version,
RID, basename, lowercase SHA-256, and positive size. It is downloaded only from
the Panel's fixed same-origin backend-asset endpoint; arbitrary URLs are not a
contract field.

Do not resend a large desired-state blob if the Agent is already on the current revision unless there is a concrete reason.

## 5. Desired state

Conceptual model:

```text
NodeDesiredState
- Revision
- Services[]
```

Each desired service minimally identifies:

```text
ServiceId
BackendType
BackendVersion policy
Enabled
ConfigSchemaVersion
Config payload
```

The frozen Phase 3 wire fields are `ServiceId`, `Name`, machine-friendly
`BackendType`, exact `BackendVersion`, `Enabled`, positive
`ConfigSchemaVersion`, and `ConfigJson`. `ConfigJson` is backend-specific JSON
validated by the compile-time provider before any file or process change.

The Agent validates the complete candidate revision first: provider existence,
artifact availability/hash, config schema, duplicate service IDs, and TCP/UDP
port conflicts. TCP and UDP port spaces are independent. It stages all
binaries/configs before commit. If any apply/start fails it restores the prior
managed snapshot and keeps the previous applied revision. A failed service is
reported independently and must not crash the Agent or unrelated services.

The Agent must never apply a desired revision partially and then report it as fully applied.

If one service fails, report detailed per-service failure and keep the last fully applied revision unless the architect explicitly defines partial-revision semantics.

## 6. Service runtime state

Normalized state should distinguish at least:

```text
Unknown
Installing
Stopped
Starting
Running
Stopping
Failed
Updating
```

Include a concise error code/message for failure without leaking secrets.

`ServiceRuntimeState` freezes `ServiceId`, normalized status, backend version,
applied config SHA-256, optional cumulative service traffic snapshot,
`ObservedAt`, and safe error code/message. Traffic counters are non-negative
monotonic totals for the current backend counter epoch; per-user accounting is
introduced in Phase 4.

## 7. Commands

Conceptual command:

```text
CommandId
NodeId
Type
TargetServiceId?
Payload?
CreatedAt
ExpiresAt?
```

Result:

```text
CommandId
Status
StartedAt
CompletedAt
ErrorCode?
ErrorMessage?
```

Agent must de-duplicate by `CommandId`.

Phase 1 supports only `RunHealthCheck`. A command contains `CommandId`, `Type`,
`CreatedAt`, and optional `ExpiresAt`. A result contains `CommandId`, status,
start/completion timestamps, and optional safe error code/message. The Agent
persists completed command IDs/results before reporting them. The Server may
redeliver a command until it observes a terminal result; repeated terminal
results update the same command record and never execute the action again.

Wire enum values use their case-sensitive names. Unknown command types are
rejected and reported as failed; they are never interpreted as arbitrary work.

Do not introduce a generic arbitrary shell command in the initial command set.

## 8. Backend types/capabilities

Stable backend IDs should be machine-friendly, e.g.:

```text
hysteria2
xray
mihomo
sing-box
```

Capability flags may include:

```text
Users
TrafficStats
HotReload
Logs
VersionQuery
MultiInbound
ConfigValidation
```

Frontend must not assume every backend exposes every capability.

Backend artifacts are published through a Server-local, operator-managed
catalog and served at
`GET /api/backend-releases/v1/assets/{fileName}`. Desired state contains only
the exact artifact identity and digest, never an arbitrary URL. The Agent
selects the one artifact matching its own RID and rejects duplicate or missing
matches.

Hysteria2 uses config schema version 1. Its JSON object contains `listenHost`,
`listenPort`, `certificatePath`, `privateKeyPath`, `authPassword`,
`masqueradeUrl`, optional `obfsPassword`, and positive `upMbps`/`downMbps`.
Unknown or duplicate JSON properties are rejected. Passwords are never emitted
in runtime state, errors, logs, or Admin list responses. Certificate/key paths
must be absolute and are read by the backend process; HyPanel does not copy
their secret contents into status data.

Xray uses config schema version 1 for one intentionally narrow profile:
VLESS over TCP with REALITY and `xtls-rprx-vision`. The JSON object contains
`listenHost`, `listenPort`, canonical UUID `clientId`, ASCII `clientEmail`,
literal `flow`, unpadded base64url `realityPrivateKey` and
`realityPublicKey`, even-length lowercase hexadecimal `shortId` (2..16
characters), DNS `serverName`, `destination` in host:port form, and
`fingerprint` (`chrome`, `firefox`, `safari`, `edge`, or `randomized`). Unknown
or duplicate properties are rejected.

The provider renders one VLESS TCP/REALITY inbound plus freedom and blocked
outbounds. `realityPrivateKey` is operational secret material and is redacted
from Admin reads and all subscriptions. Explicit subscription projection uses
only `clientId`, `flow`, `realityPublicKey`, `shortId`, `serverName`,
`fingerprint`, service name, and the configured public endpoint. Xray schema v1
capabilities are `Users`, `Logs`, `VersionQuery`, and `ConfigValidation`; it
does not claim traffic statistics or hot reload. Its listen port is TCP and
participates in whole-revision conflict validation.

Mihomo and sing-box each use config schema version 1 for the same narrow server
profile: one Shadowsocks 2022 inbound. Fields are canonical IP `listenHost`,
`listenPort` 1..65535, literal method `2022-blake3-aes-256-gcm`, standard
padded Base64 `password` decoding to exactly 32 bytes, and literal `udp: true`.
Unknown/duplicate properties are rejected. Both TCP and UDP ports are declared.
Capabilities are `Logs`, `VersionQuery`, and `ConfigValidation`; v1 does not
claim dynamic users, hot reload, or traffic stats. Subscription projection is
SIP002 `ss://base64url(method:password)@host:port#name`; operational password is
redacted from Admin reads but necessarily appears in authorized subscriptions.

## 9. Usage

The normalized domain must preserve:

```text
UserId
NodeId
ServiceId
UploadBytes
DownloadBytes
Idempotency identity / sequence
Observed window or timestamp
```

Providers may internally receive cumulative counters or deltas, but Agent-to-Server ingestion must be retry-safe.

Phase 4 freezes delta batches as the ingestion unit. `UsageBatch` contains a
random `BatchId`, an UTC `ObservedAt`, and bounded `UserUsageDelta[]` records
containing `UserId`, `ServiceId`, and non-negative upload/download byte deltas.
The Server validates that every service belongs to the authenticated Agent's
Node and every user is granted that service. A whole batch is accepted in one
transaction or rejected; partial accounting is forbidden.

The idempotency identity is `(AgentId, BatchId)`. First ingestion adds all
deltas to durable per-user/per-service totals and records the batch ID in the
same transaction. A retry returns its ID in `AcceptedUsageBatchIds` without
adding bytes again. The Agent retains unacknowledged batches across restart.
Batch identity, not time, defines duplication. Counter reset conversion into
deltas is an Agent/provider concern and always uses a new batch ID.

## 9A. Release manifest v1

The Panel serves a same-origin Agent release manifest at
`GET /api/releases/v1/manifest`. Its frozen v1 fields are:

```text
SchemaVersion: 1
Version: non-empty release version string
PublishedAt: UTC DateTimeOffset
Assets: AgentReleaseAsset[]

AgentReleaseAsset:
  Rid: supported .NET runtime identifier
  FileName: basename only; no path separators
  Sha256: 64 lowercase hexadecimal characters
  Size: positive byte count
```

Assets are downloaded from the same Panel origin through
`GET /api/releases/v1/assets/{fileName}`. The manifest never supplies an
arbitrary external URL. Installers reject unknown schema versions, duplicate
RIDs/file names, unsafe file names, size mismatches, and SHA-256 mismatches
before replacing an installed executable.

Initial Agent RIDs are `linux-x64`, `linux-arm64`, `linux-musl-x64`,
`linux-musl-arm64`, `osx-x64`, `osx-arm64`, `win-x64`, and `win-arm64`.

Panel-generated install commands contain a short-lived, one-use enrollment
token and the HTTPS Panel base URL. The Admin-authenticated response is a
sensitive credential and must not be logged or persisted after successful
enrollment. A controlled manifest-origin override is allowed, but each asset
URL remains relative to that origin and every download is hash-verified.

## 10. Users and subscriptions

User:

```text
Id
Username
PasswordHash
Role
Enabled
TrafficLimitBytes?
ExpireAt?
SubscriptionToken
CreatedAt
```

Initial roles:

```text
Admin
User
```

A user's available proxy entries are determined by `UserServiceBinding`.

Phase 4 account authentication uses opaque bearer sessions. Passwords are
stored only as versioned PBKDF2-HMAC-SHA256 records with a random per-password
salt and configurable work factor. Login returns a random 256-bit session token
once; only its SHA-256 hash is stored. Sessions expire, are revocable, and each
request rechecks the user's enabled and expiration state. The environment
bootstrap Admin token remains an independent recovery/initial-management
principal accepted only by Admin APIs. Agent authentication remains separate.

Usernames are trimmed and Unicode-normalized to Form KC for display. A separate
invariant-lowercase normalized key has a unique ordinal database constraint.
Initial roles are exactly `Admin` and `User`; authenticated Users receive 403
from Admin APIs.

Subscription endpoint concept:

```text
GET /s/<token>?format=mihomo
GET /s/<token>?format=singbox
GET /s/<token>?format=raw
GET /s/<token>?format=base64
```

Subscription token must be random, revocable, and unrelated to the user's password/session token.

Subscription tokens contain 256 random bits and only their SHA-256 hashes are
stored. Plaintext is returned only when created or rotated; rotation atomically
revokes the old token. Unknown, revoked, disabled, expired, and exhausted
subscriptions all return the same 404. Responses use `Cache-Control: no-store`,
and token values must not be logged. Formats are lowercase `raw`, `base64`,
`mihomo`, and `singbox`; Base64 is standard padded Base64 of UTF-8 raw output.
Renderers select explicit client fields and never expose whole operational
backend configs.

## 11. Error shape

Prefer one small consistent API error contract:

```text
Code
Message
TraceId?
Details? (only when safe)
```

Do not expose stack traces or backend secrets to normal clients.
## 11. Phase 7 operational convenience

Phase 7 adds Admin-only service templates. A template contains a stable ID,
NFKC-trimmed display name, invariant-lowercase unique normalized name, one of
the four supported backend IDs, exact backend version, config schema version 1,
and backend config JSON. Template read responses redact password fields and
REALITY private keys exactly like service read responses.

```text
GET    /api/admin/v1/service-templates
POST   /api/admin/v1/service-templates
PUT    /api/admin/v1/service-templates/{templateId}
DELETE /api/admin/v1/service-templates/{templateId}
POST   /api/admin/v1/nodes/{nodeId}/services/from-template/{templateId}
```

Instantiating a template creates an enabled service and increments the target
Node's desired revision in one SQLite transaction. A missing Node or template
returns 404 without mutation.

`POST /api/admin/v1/services/batch-enabled` accepts 1 through 256 unique
service IDs, each paired with its owning Node ID and target `enabled` value.
The Server validates the complete batch before writing, applies it in one
transaction, and increments each affected Node revision exactly once. Missing
services, ownership mismatch, or duplicate service IDs commit nothing.

`GET /api/admin/v1/health-summary` returns an unpersisted snapshot with
`observedAtUtc`, Node total/online/drifted counts, service
total/running/failed/stopped-or-unknown counts, and safe issues. It contains no
backend configuration or credentials. Online uses the same 30-second threshold
as Admin Node observations.
