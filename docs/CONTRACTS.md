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

## 3. Enrollment

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
AgentUpdateReport?
```

Response should eventually include:

```text
DesiredRevision
DesiredState when needed
PendingCommands
AgentUpdate when needed
Optional sync interval/config
```

Do not resend a large desired-state blob if the Agent is already on the current revision unless there is a concrete reason.

### 4.1 Agent update

Agent update is not an ordinary command because a successful apply intentionally restarts/re-executes the Agent.

```text
AgentUpdateDescriptor
- UpdateId
- Version
- Rid
- FileName
- Sha256
- Size
```

`FileName` is a validated basename from the cached release manifest. The Agent constructs
`/api/releases/v1/assets/{fileName}` against its persisted Panel origin; the Server cannot provide an arbitrary URL.
`Rid` must equal the exact build-time RID reported by the Agent and must be one of the frozen eight RIDs.

```text
AgentUpdateReport
- UpdateId
- Status: Downloading | Staged | Applying | RestartPending | Verifying | Succeeded | Failed
- TargetVersion
- Rid
- StartedAt
- PreviousVersion?
- LastError?
```

The Agent persists this report independently from command state. Repeated offers with the same `UpdateId` are
idempotent. `FinalizeInterruptedCommands` does not process Agent updates. `Succeeded` means the replacement Agent has
loaded existing identity/state and completed a Panel sync, not merely that files were copied.

Node update state keeps these values separate:

```text
ReportedAgentVersion (current)
LatestAgentVersion (release cache)
DesiredAgentVersion (requested)
AgentUpdatePolicy: Manual | Auto
```

Auto update considers stable releases only. Current >= latest never triggers a downgrade.

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
Users[] with UserId and backend credential when the provider supports multi-user identity
ControlPort? reserved by the Server for a loopback-only backend management API
```

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
Output?
```

Agent must de-duplicate by `CommandId`.

Do not introduce a generic arbitrary shell command in the initial command set.

### 7.1 Bounded service diagnostics

The first diagnostic command is `CollectServiceLogs`. It is deliberately narrower than a generic `CollectLogs`
or remote file API:

- `TargetServiceId` is required and must belong to the authenticated Agent's Node.
- The command has no caller-controlled payload, path, process arguments, environment, line count, or byte limit.
- The Agent reads only the in-memory stdout/stderr ring buffer owned by `BackendProcessSupervisor`; it never reads a
  caller-selected path and never starts a shell or backend command.
- The Supervisor retains at most 256 entries per managed service. Input is bounded while streaming, before a whole
  line is allocated: a line exceeding 4,096 UTF-16 characters is discarded in full and replaced by a fixed omission
  marker. Partial oversized content is never retained.
- The Agent replaces exact secret values found in that service's applied desired-state config with `[REDACTED]`
  before returning output.
- Successful output is UTF-8 text, limited to the newest complete log entries fitting 65,536 bytes; an entry is never
  cut in the middle. An empty buffer is a successful empty result, not an error.
- `AgentCommandResult.Output` must be null for Running and Failed results, and for successful non-diagnostic commands.
- The Server accepts at most 65,536 UTF-8 bytes, stores output only for `CollectServiceLogs`, and physically retains
  at most 20 terminal diagnostics per service. Expired unfinished diagnostics are projected as `Expired`, do not
  block a replacement collection, and are removed when a new command is created. Command/result retries remain
  idempotent by `CommandId`.
- Admin may trigger and read diagnostics; normal users cannot. Diagnostic output must never be exposed through
  subscription or normal User APIs.

## 8. Backend types/capabilities

Stable backend IDs should be machine-friendly, e.g.:

```text
hysteria2
xray
mihomo
sing-box
```

Capability flags distinguish independently:

```text
MultiUser
PerUserTraffic
TrafficStats
HotReload
Logs
VersionQuery
MultiInbound
ConfigValidation
```

Frontend must not assume every backend exposes every capability.

Backend artifacts are generated by the Server from fixed official release sources. Upstream URL, repository, archive
member and asset pattern are not caller-controlled. Agent desired state receives only a cached raw binary artifact with
`BackendType`, `Version`, exact `Rid`, basename `FileName`, lowercase SHA256 and positive `Size`.

`TrafficStats` means the provider returns real aggregate counters. `PerUserTraffic` means counters are reliably
attributable to desired users. A provider whose collector always returns no data must advertise neither flag.

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

Do not finalize a storage algorithm until the architect documents the chosen sequence/window semantics.

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

For a backend with `MultiUser`, a binding owns an encrypted, recoverable proxy credential. Create, revoke and rotate
change this credential independently from the user's login password and subscription token. Subscription output uses
the binding credential, never a shared identity read from `ServiceInstance.ConfigJson`. Disabled, expired and
traffic-limited users are omitted from desired backend users on the next reconciliation while other users and the
service remain active.

The current implemented capability matrix is:

```text
Xray:       MultiUser=true,  PerUserTraffic=true,  TrafficStats=true
Hysteria2:  MultiUser=false, PerUserTraffic=false, TrafficStats=false
sing-box:   MultiUser=false, PerUserTraffic=false, TrafficStats=false
Mihomo:     MultiUser=false, PerUserTraffic=false, TrafficStats=false
```

Unsupported backends do not receive grants that would expose a shared service secret. Their UI states the limitation.

Subscription endpoint concept:

```text
GET /s/<token>?format=mihomo
GET /s/<token>?format=singbox
GET /s/<token>?format=raw
GET /s/<token>?format=base64
```

Subscription token must be random, revocable, and unrelated to the user's password/session token.

## 11. Error shape

Prefer one small consistent API error contract:

```text
Code
Message
TraceId?
Details? (only when safe)
```

Do not expose stack traces or backend secrets to normal clients.
