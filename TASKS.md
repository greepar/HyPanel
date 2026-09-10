# HyPanel Tasks

This file is maintained by the `architect` agent.

Rules:

- Keep tasks small enough for one coherent implementation pass.
- Give every delegated task an owner.
- Parallel tasks may not edit overlapping core files.
- Mark dependencies explicitly.
- A task is complete only after its acceptance checks pass.
- Do not create hundreds of speculative tasks; expand the current and next phase only.

## Phase 0 — Foundation

### HP-000 — Inspect repository
- Owner: architect
- Depends on: none
- Scope: inspect existing files, toolchain, git state, and avoid overwriting useful work.
- Accept: repository state summarized and Phase 0 plan adjusted to reality.
- Status: DONE
- Result: Documentation-only seed repository; not yet a Git repository; no solution/source/web/CI files. .NET 10.0.400 and Node 24/npm 12 are available. Pin SDK before scaffolding because the machine default is .NET 11 Preview.

### HP-001 — Establish solution structure
- Owner: architect
- Depends on: HP-000
- Scope: create/confirm minimal Server, Agent, Shared, Web structure and project references.
- Accept: structure matches architecture; no unnecessary projects.
- Status: DONE
- Result: Added `HyPanel.slnx`, pinned .NET SDK 10.0.400, shared build defaults, and minimal Server/Agent/Shared projects with one-way references to Shared. Solution build passes with zero warnings.

### HP-002 — Server NativeAOT bootstrap
- Owner: terra
- Depends on: HP-001
- Scope: minimal ASP.NET Core Server startup/health route and AOT-safe JSON baseline.
- Accept: build + representative Linux NativeAOT publish.
- Status: DONE
- Result: Minimal `/health` endpoint uses source-generated JSON; Server enables NativeAOT/analyzers. Build, macOS x64 AOT publish, startup, and HTTP 200 smoke test pass.

### HP-003 — Agent NativeAOT bootstrap
- Owner: terra
- Depends on: HP-001
- Scope: minimal cancellable Agent host/process lifecycle suitable for future sync worker.
- Accept: build + representative Linux NativeAOT publish.
- Status: DONE
- Result: Cancellable hosted process with clean startup/shutdown lifecycle. Build, macOS x64 AOT publish, and SIGTERM graceful-exit smoke test pass.

### HP-004 — Web scaffold
- Owner: luna
- Depends on: HP-001
- Scope: lightweight TypeScript/Vite/Preact scaffold, base black-white-gray variables, Light/Dark/System theme shell.
- Accept: production web build.
- Forbidden: inventing product API contracts.
- Status: DONE
- Result: Preact/Vite strict-TypeScript shell with persistent Light/Dark/System theme and minimal monochrome responsive styling. Production build passes.

### HP-005 — Initial CI matrix
- Owner: luna
- Depends on: HP-002, HP-003
- Scope: build/test plus representative AOT CI; do not prematurely add every release workflow.
- Accept: valid workflow syntax and local commands documented.
- Status: DONE
- Result: CI validates Release build/test, Linux x64 Server and Agent NativeAOT publishes, and clean web production build. Workflow passes actionlint; README documents matching local commands.

### HP-006 — Phase 0 integration review
- Owner: architect
- Depends on: HP-002, HP-003, HP-004, HP-005
- Scope: diff review, build, AOT publish, web build, remove accidental complexity.
- Accept: Phase 0 exit criteria in ROADMAP satisfied.
- Status: DONE
- Result: Release build (0 warnings), test target, clean npm install/build, actionlint, macOS x64 Server/Agent NativeAOT generation, `/health` smoke test, and Agent SIGTERM smoke test pass. Linux x64 NativeAOT is enforced by CI because NativeAOT cannot cross-publish Linux binaries from this macOS host and Docker is unavailable.

## Phase 1 — Agent control plane

Do not expand implementation details until HP-006 is complete.

Planned slices:

### HP-100 — Freeze Phase 1 shared contracts
- Owner: architect
- Depends on: HP-006
- Scope: freeze minimal enrollment/sync/metrics/revision/command contracts, wire AOT JSON source generation, and document authentication plus retry semantics.
- Accept: Shared builds with AOT analyzers; docs and C# contracts agree; no persistence or backend-provider API leaks into Shared.
- Status: DONE
- Result: Froze v1 enrollment, metrics, revision and `RunHealthCheck` command contracts with source-generated JSON. Defined independent Agent bearer authentication, hashed secret/token storage, atomic enrollment, command redelivery, and de-duplication semantics. Service/backend/usage contracts remain deferred.

### HP-101 — SQLite minimal schema/persistence
- Owner: terra
- Depends on: HP-100
- Scope: explicit-SQL SQLite migration runner and repositories for Nodes, Agents, enrollment tokens, latest metrics, revisions, and commands according to `docs/ARCHITECTURE.md`.
- Accept: fresh database migrates idempotently; foreign keys/WAL enabled; parameterized persistence supports atomic enrollment and retry-safe command result updates; Server build/AOT publish pass.
- Forbidden: changing Shared contracts or schema design; EF Core; admin/enrollment/sync HTTP endpoints.
- Status: DONE
- Result: Added explicit parameterized SQLite persistence, connection-scoped foreign-key enforcement and WAL, idempotent schema migration, Node/revision/metrics/command operations, hashed enrollment credentials, atomic one-use enrollment, and terminal command idempotency. Release build and macOS x64 Server NativeAOT publish pass; native startup creates schema version 1 and repeated startup keeps one migration row.

### HP-102 — Enrollment server flow
- Owner: terra
- Depends on: HP-101
- Scope: protected Admin endpoints create Nodes and issue 15-minute enrollment tokens; public Agent enrollment consumes the token and returns one-time credentials.
- Accept: unauthorized Admin calls fail uniformly; enrollment succeeds once and replay/expiry fail without sensitive detail; API uses source-generated JSON and NativeAOT publishes.
- Status: DONE
- Result: Added fail-fast Admin bootstrap bearer authentication, protected Node creation and fixed 15-minute enrollment-token issuance, and public one-use Agent enrollment. Server build/AOT pass; smoke test proves Admin 401, enrollment 200, replay 401, and only 32-byte token/secret hashes are persisted.

### HP-103 — Agent enrollment client/credential persistence
- Owner: terra
- Depends on: HP-100
- Status: DONE
- Result: Agent enrolls only when credentials are absent, requires HTTPS except localhost Development, uses AOT-safe JSON, atomically persists credentials, applies owner-only Unix permissions, and never logs secrets. Build and macOS x64 NativeAOT publish pass.

### HP-104 — Agent sync server endpoint
- Owner: terra
- Depends on: HP-101, HP-102
- Scope: authenticate Agent ID + bearer secret, validate sync metrics/results, update last-seen/report state and command results, and return revision plus pending commands.
- Accept: invalid auth and malformed reports are rejected safely; desired state is omitted when current; command retry does not duplicate completion; build/AOT and HTTP round-trip pass.
- Status: DONE
- Result: Added fixed-time Agent authentication, strict sync validation, atomic report/metrics/result ingestion, revision-aware desired-state response, and active health-check command delivery. Removed the unsafe non-owned result update path. Build/AOT pass; HTTP smoke proves wrong-secret 401, current revision omits desired state, and differing revision returns it.

### HP-105 — Agent sync loop
- Owner: terra
- Depends on: HP-103, HP-104
- Scope: authenticated polling, basic cross-platform metrics, Phase 1 revision application, persisted command de-duplication/results, bounded retry, and `RunHealthCheck` execution.
- Accept: enrolled Agent reconnects, updates last-seen, applies a revision, executes a command once across restart/retry, and survives temporary Server failure; Agent build/AOT pass.
- Status: DONE
- Result: Added authenticated polling, bounded exponential retry, strict credential rejection, cross-platform metrics, atomic revision state, persistent command Running/terminal state, and bounded completed-ID de-duplication. Fixed CPU normalization and platform RID reporting. Build/AOT pass; real local E2E proves enrollment, online sync, health-check success, and no re-execution after Agent restart.

### HP-104A — Phase 1 Admin observation/command surface
- Owner: terra
- Depends on: HP-104
- Scope: protected Admin read API for Node/Agent online state and one endpoint that queues `RunHealthCheck`.
- Accept: Admin can observe last-seen/revisions and trigger the one approved command without generic remote execution.
- Status: DONE
- Result: Added protected Node/Agent observation with 30-second online state and a fixed health-check-only command endpoint with five-minute expiry. Build/AOT and local E2E pass; no generic command or shell surface exists.

### HP-106 — Repetitive DTO/serialization/tests
- Owner: luna
- Depends on: HP-100
- Status: DONE
- Result: Added 16 focused Shared serialization, Server SQLite security/idempotency, and Agent state/metrics tests. All contain concrete assertions and pass; real-process E2E additionally covers HTTP enrollment, sync, command completion, and restart de-duplication.

### HP-107 — Phase 1 integration/security review
- Owner: architect
- Depends on: HP-101 through HP-106
- Scope: local and remote end-to-end enrollment/authentication/sync/revision/command/security verification, full build/test/Web/AOT gates, and operational deployment evidence.
- Accept: Phase 1 ROADMAP exit criteria pass on a US Server and at least two remote Agents; no plaintext credential or generic remote shell path exists.
- Status: DONE
- Result: Full local gate passes (0-warning Release build, 16/16 tests, Web, actionlint, macOS and cross-compiled Linux NativeAOT). Deployed HTTPS Server in US and Agents on AlmaLinux/Debian; enrollment, reconnect, online state, revision, two health-check commands, restart de-duplication, wrong-auth rejection, outage backoff/recovery, and hashed credentials pass. TLS reuses Lucky-managed certificates through a least-privilege copy plus systemd path refresh.

## Phase 2 — Cross-platform installation

### HP-200 — Freeze release/install contracts
- Owner: architect
- Depends on: HP-107
- Scope: versioned release manifest, same-origin asset rules, SHA-256 verification, installer configuration inputs, and one-line command security semantics.
- Status: DONE
- Result: Froze same-origin manifest/assets, eight initial Agent RIDs, strict basename/size/SHA-256 validation, and secret-safe one-line commands. Enrollment tokens never appear in installer URLs or public script content.

### HP-201 — Release distribution Server API
- Owner: terra
- Depends on: HP-200
- Scope: AOT-safe manifest/asset endpoints and protected one-line installation command generation.
- Status: DONE
- Result: Added strict AOT-safe release catalog, same-origin manifest/assets and fixed installer scripts, plus Admin-protected secret-safe one-line command generation. Build/AOT, traversal/unlisted 404, and live US HTTPS serving pass.

### HP-202 — Unix installer
- Owner: terra
- Depends on: HP-200
- Scope: Linux/macOS/OpenWrt detection, verified download, atomic install, and systemd/OpenRC/procd/LaunchDaemon registration.
- Status: DONE
- Result: Added POSIX installer with OS/arch/libc detection, strict complete-manifest validation, verified safe archive extraction, atomic rollback, systemd/OpenRC/procd/LaunchDaemon registration, dedicated Linux user, and post-enrollment token removal. shellcheck, local staging/tamper tests, and real Debian one-click install/upgrade pass.

### HP-203 — Windows installer
- Owner: luna
- Depends on: HP-200
- Scope: PowerShell architecture detection, verified download, atomic install, and Windows Service registration.
- Status: DONE
- Result: Added PowerShell 5.1+ win-x64/win-arm64 installer with strict manifest/ZIP validation, size/hash verification, safe extraction, Windows Service environment, token cleanup, backup and rollback. Parser validation passes; Windows runtime execution remains a CI/Windows-host gate.

### HP-204 — Release packaging automation
- Owner: luna
- Depends on: HP-200
- Scope: deterministic multi-RID NativeAOT packaging and manifest generation with hashes/sizes.
- Status: DONE
- Result: Added deterministic cross-platform packaging and GitHub Release workflow. AotAnywhere produced all 8 Agent and 4 Server NativeAOT RID archives locally; layouts, hashes, sizes, manifest, shellcheck and actionlint pass.

### HP-205 — Phase 2 integration review
- Owner: architect
- Depends on: HP-201 through HP-204
- Scope: full build/test/AOT/script/workflow gates plus live same-origin release distribution and one-click installation.
- Status: DONE
- Result: Full gate passes: 0-warning solution build, 27/27 tests, Web build, shellcheck, PowerShell parser, actionlint, all 8 Agent and 4 Server NativeAOT packages, strict archive layouts and hashes. Live US release API and fresh Debian one-click install/upgrade prove enrollment, systemd registration, token cleanup, online sync, command execution, same-origin delivery, traversal rejection, and tamper-safe preservation of the prior install.

## Phase 3 — Hysteria2 and multi-service reconciliation

### HP-300 — Freeze service/backend contracts
- Owner: architect
- Depends on: HP-205
- Scope: Shared service desired state/artifact wire contract and Agent-side `IBackendProvider` public API, lifecycle, validation, health and usage semantics.
- Status: DONE
- Result: Froze Phase 3 desired service/artifact/runtime contracts and Agent-side `IBackendProvider`. Providers validate/render/specify/observe only; shared infrastructure owns same-origin hash-verified acquisition, atomic files, process lifetime and rollback. Complete-revision preflight/commit semantics and Hysteria2 config schema v1 are documented.

### HP-301 — Service persistence and Admin API
- Owner: terra
- Depends on: HP-300
- Scope: SQLite services/artifacts schema, revision mutation, Admin CRUD and backend asset distribution.
- Status: DONE
- Result: SQLite service/runtime persistence, monotonic revision CRUD, authenticated backend asset distribution and password-redacted Admin responses.

### HP-302 — Agent backend binary/process infrastructure
- Owner: terra
- Depends on: HP-300
- Scope: verified same-origin binary acquisition, per-instance directories, process supervision and failure isolation.
- Status: DONE
- Result: Same-origin hash-verified acquisition, isolated service directories, supervised processes, restart recovery and removed-instance cleanup.

### HP-303 — Hysteria2 provider
- Owner: terra
- Depends on: HP-300, HP-302
- Scope: schema-v1 config validation/rendering, binary validation, lifecycle and health.
- Status: DONE
- Result: Hysteria2 2.12.2 strict config validation, YAML render, process specification and normalized health; unsupported per-user traffic is not fabricated.

### HP-304 — Multi-service reconciliation
- Owner: terra
- Depends on: HP-301 through HP-303
- Scope: revision-atomic desired-state application, port conflict validation, independent instance reconciliation and normalized actual state.
- Status: DONE
- Result: Full revision preflight, TCP/UDP-aware conflicts, rollback and independent service lifecycle are implemented and tested.

### HP-305 — Phase 3 UI
- Owner: luna
- Depends on: HP-301
- Scope: Node/service overview and Hysteria2 create/edit/status controls using the established Preact shell.
- Status: DONE
- Result: Preact Node/service management and runtime display with Light/Dark/System themes.

### HP-306 — Phase 3 tests
- Owner: luna
- Depends on: HP-301 through HP-304
- Status: DONE
- Result: Provider, infrastructure, reconciliation, rollback, process-exit and managed-directory tests pass.

### HP-307 — Phase 3 integration review
- Owner: architect
- Depends on: HP-301 through HP-306
- Status: DONE
- Result: US Server + UK Agent verified dual Hysteria2 instances, isolated update, conflict rejection preserving old processes/revision, disable/delete isolation, restart recovery and directory cleanup.

## Phase 4 — Users, subscriptions and usage accounting

### HP-400 — Freeze account, subscription and usage contracts
- Owner: architect
- Status: DONE
- Result: Versioned PBKDF2 accounts, hashed opaque sessions/subscription tokens, explicit service grants/public endpoints and transactional `(AgentId, BatchId)` usage deltas.

### HP-401 — User and grant persistence/API
- Owner: terra
- Depends on: HP-400
- Status: DONE
- Result: SQLite migrations v4-v5, User/Admin auth, centralized Admin authorization, CRUD, grants, public endpoints and usage queries.

### HP-402 — Retry-safe usage accounting
- Owner: terra
- Depends on: HP-400
- Status: DONE
- Result: Atomic receipt/total updates, overflow rollback, explicit acknowledgement and durable Agent pending queue.

### HP-403 — Subscription generation
- Owner: architect
- Depends on: HP-401
- Status: DONE
- Result: Revocable one-time tokens and explicit Hysteria2 projection to raw, Base64, Mihomo and sing-box without leaking operational config.

### HP-404 — Phase 4 UI
- Owner: luna
- Depends on: HP-401, HP-403
- Status: DONE
- Result: Account/bootstrap login, user/grant/public-endpoint management, self-service usage and one-time subscription links. Fake QR is intentionally omitted.

### HP-405 — Phase 4 integration review
- Owner: architect
- Depends on: HP-401 through HP-404
- Status: DONE
- Result: 91/91 local tests plus US+UK DB Admin/User isolation, four subscription formats, token/session revocation and duplicate usage-batch no-double-count evidence.

## Phase 5 — Xray

### HP-500 — Freeze Xray schema and capabilities
- Owner: architect
- Status: DONE
- Result: Frozen VLESS/TCP/REALITY/Vision schema v1 with strict key/host/UUID validation and evidence-based capability flags.

### HP-501 — Implement Xray provider and artifacts
- Owner: terra
- Depends on: HP-500
- Status: DONE
- Result: Official Xray 26.3.27 artifact, strict provider, deterministic runtime config, process/health integration and 71 Agent tests.

### HP-502 — Extend Admin/service API and subscriptions for Xray
- Owner: architect
- Depends on: HP-500
- Status: DONE
- Result: Generic service CRUD, private-key redaction, Xray Admin UI and mixed raw/Base64/Mihomo/sing-box subscription projection.

### HP-503 — Validate Hysteria2 and Xray coexistence on UK Agent
- Owner: architect
- Depends on: HP-501, HP-502
- Status: DONE
- Result: US Server + UK Agent verified Hysteria2 UDP and Xray TCP coexistence, mixed subscriptions, isolated update, conflict rollback, disable/enable/delete and dependency cleanup.

## Phase 6 — Mihomo and sing-box

### HP-600 — Freeze Mihomo and sing-box schemas/capabilities
- Owner: architect
- Status: DONE
- Result: Frozen shared Shadowsocks 2022 schema v1 with strict 32-byte Base64 key and TCP+UDP ownership.

### HP-601 — Implement Mihomo provider and artifact
- Owner: terra
- Depends on: HP-600
- Status: DONE
- Result: Official Mihomo 1.19.30 artifact, deterministic YAML provider, lifecycle/health and focused tests.

### HP-602 — Implement sing-box provider and artifact
- Owner: terra
- Depends on: HP-600
- Status: DONE
- Result: Official sing-box 1.14.0 artifact, deterministic JSON provider, lifecycle/health and focused tests.

### HP-603 — Extend generic Admin UI/API for Phase 6 backends
- Owner: luna
- Depends on: HP-600
- Status: DONE
- Result: Generic four-backend create/edit UI and SIP002 raw/Base64/Mihomo/sing-box projections.

### HP-604 — Validate multi-backend coexistence on UK Agent
- Owner: architect
- Depends on: HP-601 through HP-603
- Status: DONE
- Result: US+UK verified Hysteria2 + Mihomo + sing-box coexistence, two SIP002 entries, isolated Mihomo config update and independent cleanup.

## Phase 7 — Operational convenience

### HP-700 — Freeze operational convenience scope
- Owner: architect
- Status: DONE
- Result: Froze templates, transactional batch service desired state, aggregate health, and embedded Web delivery. Agent binary batch updates, backend channels, signing, and mTLS are deferred pending a dedicated trust/rollback protocol.

### HP-701 — Implement service templates
- Owner: terra
- Depends on: HP-700
- Status: DONE
- Result: SQLite migration v6, secret-redacted Admin CRUD, and revision-atomic service instantiation.

### HP-702 — Implement transactional batch desired-state operations
- Owner: terra
- Depends on: HP-700
- Status: DONE
- Result: Up to 256 unique Node/service ownership pairs are validated and changed atomically, with one revision increment per affected Node and full rollback on invalid input.

### HP-703 — Implement operational health view and Web controls
- Owner: luna
- Depends on: HP-701, HP-702
- Status: DONE
- Result: Added health/drift snapshot, template and batch controls, and preserved user/grant/public-endpoint administration in the Preact UI.

### HP-704 — Final integration and deployment validation
- Owner: architect
- Depends on: HP-701 through HP-703
- Status: IN_PROGRESS
