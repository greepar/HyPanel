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
- Status: DONE
- Result: 177/177 tests, zero-warning Release build, Web build, linux-x64 Server/Agent NativeAOT, US embedded-Web HTTP/browser validation with zero console errors, and UK lifecycle/cleanup confirmation.

## Phase 8 — Admin workspace layout

### HP-800 — Adapt reference dashboard layout
- Owner: terra
- Status: DONE
- Result: Split the Admin UI into 概览、服务、模板、用户 workspaces with desktop sidebar, sticky workspace header, responsive mobile navigation, complete Simplified Chinese UI text, and preserved monochrome themes and behavior.

### HP-801 — Release version and artifact publishing
- Owner: architect
- Status: DONE
- Result: Tag-driven semantic version validation and GitHub Release publishing verified with `v0.2.0`; 17 release assets, 8 Agent RIDs, 4 Server RIDs, manifest, and SHA256SUMS published.

## Frontend Rescue

> The implementation and history were complete through HP-801 when this rescue began. The shipped Server APIs
> remain authoritative; this work changes neither the Server-Agent contract nor the database schema.

### FE-000 — Audit shipped frontend and API surface
- Owner: architect
- Depends on: HP-801
- Scope: reconcile repository state, reference images, navigation, frontend behavior, and Server APIs.
- Accept: rescue boundaries and concrete defects are recorded; unrelated dirty work is preserved.
- Status: DONE
- Result: Found a 573-line single-component UI with button-only pseudo-routing, eager all-admin loading,
  node-scoped grants, uninitialized public endpoints, missing Node install flows, incomplete CRUD, global error
  state, and weak loading/empty feedback. Confirmed all available Admin/User endpoints and preserved unrelated work.

### FE-001 — Establish frontend module and API boundaries
- Owner: architect
- Depends on: FE-000
- Scope: split domain models, backend configuration mapping, HTTP/session client, and reusable primitives.
- Accept: no product API contract is invented; TypeScript strict build passes.
- Status: DONE
- Result: Split the app into domain, API, backend mapping, router, shared UI, Admin pages and User subscription
  modules. A single authenticated API client owns response/error handling.

### FE-002 — Implement addressable role-aware navigation
- Owner: architect
- Depends on: FE-001
- Scope: hash routing compatible with embedded static delivery, browser history, Admin/User route ownership.
- Accept: reload/back/forward and role guards resolve to stable pages.
- Status: DONE
- Result: Added addressable overview, nodes, services, templates, users and subscription routes. Role guards keep
  normal users out of Admin routes and prevent Admin requests from being issued by the User workspace.

### FE-003 — Rebuild application shell and async feedback
- Owner: architect
- Depends on: FE-001, FE-002
- Scope: responsive shell, theme control, page headers, loading/error/success/empty states and confirmations.
- Accept: keyboard/focus semantics and mobile navigation are usable in all themes.
- Status: DONE
- Result: Rebuilt the shell and shared Page, Modal, Notice, Loading, Empty and Stat primitives with responsive
  navigation, explicit progress, inline feedback, confirmations and persistent Light/Dark/System themes.

### FE-004 — Rescue overview and Node onboarding
- Owner: architect
- Depends on: FE-003
- Scope: health overview, Node inventory, Node creation, installer generation/copy, and safe health-check action.
- Accept: the existing Node and installer APIs are fully reachable from the UI.
- Status: DONE
- Result: Added health and convergence overview, Node inventory/create, Unix/PowerShell one-time install command
  delivery, copy warnings, last-seen state and the fixed RunHealthCheck action.

### FE-005 — Rescue service management
- Owner: architect
- Depends on: FE-003
- Scope: node-aware service list, endpoint loading/editing, create/edit/delete/enable/batch flows, backend forms.
- Accept: all four supported backends remain distinguishable; destructive actions require confirmation.
- Status: DONE
- Result: Added complete node-scoped service lifecycle and batch controls, lazy public-endpoint loading, runtime
  errors and backend-specific forms for Hysteria2, Xray, Mihomo and sing-box.

### FE-006 — Rescue service templates
- Owner: architect
- Depends on: FE-005
- Scope: template inventory, instantiation target selection, deletion, and service-to-template creation flow.
- Accept: template operations use existing API semantics and refresh predictably.
- Status: DONE
- Result: Added template inventory, service-to-template creation, explicit target-node instantiation and confirmed
  deletion using the existing Admin API.

### FE-007 — Rescue users, grants, and usage
- Owner: architect
- Depends on: FE-003
- Scope: user CRUD, token rotation, cross-node service grants, and Admin usage visibility.
- Accept: grants are not incorrectly limited to whichever Node was last selected.
- Status: DONE
- Result: Added User CRUD, limits/expiry/status, usage totals, confirmed token rotation and grants loaded across all
  Nodes rather than the previously selected Node only.

### FE-008 — Rescue normal-user subscription workspace
- Owner: architect
- Depends on: FE-003
- Scope: account limits, detailed usage, one-time token presentation, format links, QR and copy feedback.
- Accept: normal users cannot navigate to or trigger Admin data loads.
- Status: DONE
- Result: Added role-isolated account/usage views and one-time Raw, Base64, Mihomo and sing-box links with real QR
  generation and copy feedback.

### FE-009 — Responsive, accessibility, and visual-system pass
- Owner: architect
- Depends on: FE-004 through FE-008
- Scope: reference-aligned monochrome tokens, responsive layouts, focus states, dialogs, labels, reduced motion.
- Accept: desktop and narrow viewport browser review passes in Light/Dark/System themes.
- Status: DONE
- Result: Rebuilt the monochrome visual system, desktop sidebar and mobile navigation; added visible focus,
  reduced-motion support, responsive cards/tables/forms, modal focus entry/trap/restore and Escape handling. Browser
  review passed at 1280px and 390px in Light, Dark and System modes without horizontal overflow.

### FE-010 — Frontend integration gate
- Owner: architect
- Depends on: FE-001 through FE-009
- Scope: diff review, TypeScript/Web build, .NET build/tests, embedded-Web build, browser and console verification.
- Accept: production Web and Server embedding build; key Admin/User journeys render without console errors.
- Status: DONE
- Result: Clean `npm ci` and production Web builds pass; Release solution build succeeds with zero warnings;
  Shared 6/6, Server 60/60 and Agent 111/111 tests pass (177 total); osx-x64 Server NativeAOT publish embeds the
  production Web assets. Mock-backed browser journeys verified Admin page/API boundaries, cross-Node grants,
  User route isolation, history navigation, Node/service empty states, endpoint 204 semantics and real subscription
  QR generation with zero console errors. Scoped diff review and whitespace checks pass.

## Phase 9 — Operational diagnostics

> The remaining Phase 7 items are not equally safe to bundle. Agent binary updates, backend update channels,
> update signing and stronger Agent identity require a dedicated trust and rollback protocol and remain deferred.
> Phase 9 therefore advances the already-approved “better logs/health views” scope without arbitrary shell access.

### HP-900 — Audit diagnostics data and freeze the first slice
- Owner: architect
- Depends on: FE-010
- Scope: inspect current command, runtime error and persistence surfaces; define the smallest bounded diagnostic
  operation that is useful across providers and compatible with outbound Agent polling.
- Accept: no generic command or shell surface; payload bounds, retention, authorization and retry semantics are explicit.
- Status: DONE
- Result: Froze additive `CollectServiceLogs`/`TargetServiceId`/successful `Output` contracts. Collection is limited
  to the Supervisor-owned 256-entry memory ring, 4,096 characters per captured line and 65,536 UTF-8 bytes per
  result, with exact applied-config secret redaction. No payload, arbitrary path, process argument or shell exists;
  Admin-only access, ownership checks, retention and CommandId retry idempotency are required.

### HP-901 — Implement bounded Agent log collection
- Owner: architect
- Depends on: HP-900
- Scope: add one fixed log-collection command and bounded result transport using existing polling/de-duplication.
- Accept: per-service collection cannot escape managed directories, exceed fixed limits or crash reconciliation.
- Status: DONE
- Result: Added `CollectServiceLogs` handling in `SyncWorker` plus `ServiceLogCollector`, which reads only the
  Supervisor-owned per-service memory ring, truncates captured lines to 4,096 characters, redacts exact applied
  config secrets and keeps the newest content within 65,536 UTF-8 bytes without splitting surrogate pairs.
  Unmanaged services return `service_not_managed`; missing target returns `target_required`; interrupted commands are
  finalized as before. Seven focused collector tests cover bounds, secret extraction, redaction and empty buffers.

### HP-902 — Persist and expose recent diagnostics
- Owner: architect
- Depends on: HP-900, HP-901
- Scope: bounded SQLite retention and Admin-only query/trigger endpoints.
- Accept: retries are idempotent; expired diagnostics are removed; secrets and arbitrary filesystem content are absent.
- Status: DONE
- Result: Added migration v7 (`target_service_id`, `output`, service/type index), Agent-owned command creation with
  single active command per service, 20-record terminal retention with expired Pending/Running pruning, and
  `GetServiceDiagnosticsAsync`. `RecordCommandResultAsync` now rejects the whole sync transaction if `Output`
  accompanies anything other than a successful `CollectServiceLogs` result instead of silently discarding it.
  Admin-only POST/GET diagnostics endpoints enforce Node/service ownership (404) and active-command conflict (409).

### HP-903 — Diagnostics UI
- Owner: architect
- Depends on: HP-902
- Scope: add a service-owned diagnostics interaction to the rescued Admin workspace.
- Accept: loading, empty, expired and failure states are explicit; normal users issue no diagnostics requests.
- Status: DONE
- Result: Added a per-service diagnostics modal showing Pending/Running/Succeeded/Failed records, bounded log
  output, failure messages and an empty state, with collection disabled while a command is active. Verified at
  1280px and 390px with zero console errors and no Admin diagnostics requests issued from the User workspace.

### HP-904 — Phase 9 integration/security review
- Owner: architect
- Depends on: HP-901 through HP-903
- Scope: retry/restart/bounds/auth/browser/AOT verification and final architectural review.
- Accept: no generic remote execution or unbounded log/database growth; full local gates and representative AOT pass.
- Status: DONE
- Result: Release build passes with zero warnings. 190/190 tests pass (Shared 9, Server 63, Agent 118), including
  new assertions for JSON round-tripping, output bounds, secret redaction, migration v7, ownership, forged-output
  rejection and terminal retention. Agent and Server osx-x64 NativeAOT publishes both succeed. Browser review
  confirmed diagnostics states, duplicate-collection prevention, narrow-viewport layout and zero console errors.
  No arbitrary path, shell, payload or unbounded growth path was introduced.

## Phase 10 — Node telemetry visibility

> Audit finding: every Agent sync writes `latest_metric_snapshot_json`, but no Server code ever reads it and no Admin
> endpoint returns `NodeMetrics`. `docs/CONTEXT.md` promises Administrators CPU/RAM/disk/network basics, so the data
> is already collected and persisted yet invisible. This phase exposes it read-only: no Server-Agent contract change,
> no schema change, and no new mutation path.

### HP-1000 — Freeze telemetry read slice
- Owner: architect
- Depends on: HP-904
- Scope: expose the already-persisted latest `NodeMetrics` through the existing Admin node observation response and UI.
- Accept: reuse the Shared `NodeMetrics` contract; remain read-only; corrupted or absent snapshots degrade to null.
- Status: DONE
- Result: Froze a read-only slice. `NodeMetrics` is reused verbatim from `HyPanel.Shared`; no new DTO, no Server-Agent
  contract change and no migration. Absent or malformed `latest_metric_snapshot_json` must resolve to null rather
  than failing node listing. Limits are explicit: only the newest snapshot is surfaced, and only to Admin.

### HP-1001 — Expose latest Node metrics through the Admin API
- Owner: architect
- Depends on: HP-1000
- Scope: read `latest_metric_snapshot_json` in `GetNodeObservationsAsync` and include metrics in the observation response.
- Accept: absent or malformed snapshots return null instead of failing the request; response stays AOT-serializable.
- Status: DONE
- Result: `NodeObservationRecord` now carries the snapshot, `GetNodeObservationsAsync` selects it, and
  `AdminNodeObservationResponse` exposes a nullable `Metrics`. `AdminObservationEndpoints.TryReadMetrics` deserializes
  through the source-generated context and returns null on empty or malformed JSON. `NodeMetrics` was registered on
  `ServerJsonSerializerContext`, and the now-unused `System.Text.Json` using was removed from `ServerContracts`.

### HP-1002 — Show node telemetry in the Admin workspace
- Owner: architect
- Depends on: HP-1001
- Scope: render CPU, memory, disk, uptime, network and observation time per node, with a clear "not yet reported" state.
- Accept: desktop and narrow layouts stay scannable; no metrics are requested from a normal-user session.
- Status: DONE
- Result: `Node.metrics` is typed in the Web domain, and each node with a snapshot renders a telemetry row showing CPU,
  used/total memory, used/total disk, uptime, upload/download and observation time. Nodes without a snapshot render no
  row. Added byte/uptime formatters and 6/3/2-column responsive rules.

### HP-1003 — Phase 10 integration review
- Owner: architect
- Depends on: HP-1001, HP-1002
- Scope: unit/browser/AOT verification that telemetry is visible and read-only.
- Accept: full local gates pass; a node without a snapshot renders a safe empty state.
- Status: DONE
- Result: Release build passes with zero warnings; 192/192 tests pass (Shared 9, Server 65, Agent 118) including new
  assertions that a synced snapshot is read back intact and that missing/malformed snapshots return null without
  throwing. Server osx-x64 NativeAOT publish succeeds. Browser review confirmed formatted telemetry, no row for a
  node without metrics, 0 horizontal overflow at 1280px and 390px, and zero console errors.

## Acceptance remediation

> A second independent audit reopened completion claims that were supported by build/manual smoke evidence but not
> by durable regression coverage. Historical DONE results above remain as execution history; current acceptance is
> governed by AR-001 through AR-004 below.

### AR-001 — Diagnostics safety and lifecycle remediation
- Owner: architect
- Reopens: HP-900 through HP-904
- Status: DONE
- Accept: bounded-before-allocation log capture, complete-entry output, no truncation-boundary secret leak, explicit
  expiry, physical 20-record retention, and service deletion after diagnostics all have regression tests.
- Result: Agent capture is bounded per entry before retention, and output keeps only complete newest entries inside the
  UTF-8 cap after exact applied-secret redaction. Regression tests cover oversized input, entry boundaries, Unicode,
  redaction, unmanaged services and empty buffers. Server tests cover explicit command expiry, transactional result
  validation, physical 20-record terminal retention and atomic service deletion with diagnostic commands.

### AR-002 — Frontend correctness and accessibility remediation
- Owner: architect
- Reopens: FE-003, FE-005, FE-007 through FE-010
- Status: DONE
- Accept: durable load errors/retry, node-switch cancellation, redacted-secret preservation, UTC expiration, token
  rotation exclusion, validated restored/bootstrap sessions, keyboard format selection and accessible progress/focus.
- Result: Added durable loading/error/retry states, node-switch cancellation, redacted-secret merge preservation,
  local `datetime-local` to UTC conversion, validated account/bootstrap restoration and canonical session cleanup.
  Token rotation now uses a synchronous in-flight lock in addition to disabled/loading UI. Real embedded-Web browser
  review proved one POST under forced repeat clicks for User and Admin flows, keyboard radio selection with QR sync,
  progressbar semantics, Light/Dark/System themes, modal usability and no horizontal overflow at 390px.

### AR-003 — Node telemetry semantics remediation
- Owner: architect
- Reopens: HP-1001 through HP-1003
- Status: DONE
- Accept: host/container CPU, memory, uptime and network semantics are truthful; corrupt snapshots and missing telemetry
  degrade explicitly; NativeAOT and browser rendering pass.
- Result: Metrics collection reports normalized host/container CPU, bounded available/total memory, host/container
  uptime rather than Agent process lifetime, aggregate network counters and disk capacity. Regression tests assert
  invariants and uptime semantics; malformed/missing persisted snapshots return null. Real Nodes UI rendered CPU,
  memory, disk, uptime and network values at 390px, and Agent/Server osx-x64 NativeAOT publishes passed.

### AR-004 — Final independent acceptance gate
- Owner: architect
- Depends on: AR-001 through AR-003
- Status: DONE
- Accept: clean Web install/build, zero-warning Release build, all tests, Agent+Server NativeAOT, real embedded-Web
  browser journeys and scoped diff review pass; only then may current completion be claimed.
- Result: `npm ci` reported zero vulnerabilities and the production Web build passed. Release solution build passed
  with zero warnings/errors; Shared 9/9, Server 70/70 and Agent 121/121 tests passed (200 total). Agent and Server
  osx-x64 NativeAOT publishes and `git diff --check` passed. A fresh embedded-Web tab loaded the current hashed bundle;
  account/bootstrap login, role-tamper recovery, subscriptions, User/Admin token exclusion, telemetry, themes and 390px
  layouts passed with zero browser console errors or warnings. The local acceptance gate is complete.

## Operational rollout

### OP-001 — Deploy accepted build to US and retire UK Agent
- Owner: architect
- Depends on: AR-004
- Status: DONE
- Scope: deploy the accepted Linux x64 NativeAOT Server, move its ingress behind the existing same-host Lucky HTTPS
  proxy, deploy/test the UK Agent, then uninstall that Agent when requested while retaining a recovery backup.
- Result: US Server runs the accepted binary behind Lucky at `https://us.greepar.uk`, bound only to
  `http://127.0.0.1:5291`; direct 8443 TLS and the HyPanel certificate-refresh path are disabled. Public health,
  login and Raw/Base64/Mihomo/sing-box subscription responses pass. UK Agent deployment initially synced and restored
  its managed Hysteria2 process, then was disabled and removed on request; its live data remains under
  `/var/lib/hypanel-agent` and a root-only full recovery backup was retained.

### OP-002 — Repair node onboarding behind Lucky and simplify navigation
- Owner: architect
- Depends on: OP-001
- Status: DONE
- Scope: restore install-command generation behind the same-host HTTPS reverse proxy and remove duplicated/sidebar
  branding and single-character navigation marks.
- Result: Node creation was succeeding, but install-command generation rejected Lucky's HTTP upstream scheme in
  Production. The Server now accepts one layer of forwarded host/protocol/address headers exclusively from IPv4/IPv6
  loopback, preserving the external `https://us.greepar.uk` origin without trusting public spoofed headers. A focused
  regression test freezes that trust boundary. The top bar is the sole HyPanel brand, and sidebar navigation is plain
  text without letter icons. Web build, 71/71 Server tests, linux-x64 NativeAOT and whitespace checks pass. Public UI
  verification confirmed the install modal emits the correct HTTPS installer URL, the add-node dialog opens, and the
  browser has zero console errors or warnings.

### OP-003 — One-line enrollment bootstrap URLs
- Owner: architect
- Depends on: OP-002
- Status: DONE
- Scope: replace verbose environment-prefixed install commands with copy-friendly one-line Shell and PowerShell
  commands without weakening enrollment-token entropy, expiry or single-use behavior.
- Result: Install commands now use `curl -Ls https://<panel>/i/<6-digits> | bash` and
  `irm https://<panel>/i/<6-digits> | iex`. The six-digit value is a 15-minute, single-redemption, in-memory lookup
  protected by a per-IP fixed-window limiter; it resolves to the existing 256-bit enrollment token, so Agent identity
  security is not reduced to six digits. Bootstrap responses remain platform-bound, no-store/no-referrer, and load the
  canonical installer with credentials scoped to that process. Public browser verification confirmed the exact short
  command shape.

### OP-004 — Restore UK Agent and eliminate stale service status
- Owner: architect
- Depends on: OP-003
- Status: DONE
- Scope: publish the current Agent build, make same-node reinstall safe, restore UK sync, and prevent stale runtime
  observations from presenting an offline service as running.
- Result: Published a hash-verified 0.2.1 manifest with all eight NativeAOT Agent RIDs and deployed the matching UK
  linux-x64 binary. Installers now back up identity/control state, force re-enrollment when a fresh token is supplied,
  and restore prior state on failure. Same-node enrollment atomically rotates the existing Agent secret and preserves
  Agent identity instead of violating the node uniqueness constraint. UK is online and has sustained successful syncs
  without warnings. The Services page suppresses cached runtime state for offline nodes; public verification shows the
  old `uk-agent` service as `未知` rather than `运行中`. Release build is warning-free and 206/206 tests pass.

### OP-005 — Complete Agent self-update lifecycle
- Owner: architect (Sol)
- Depends on: OP-004
- Status: DONE
- Scope: complete the GitHub Release → Server cache → desired update offer → Agent verified staged replacement →
  post-restart sync verification lifecycle for all eight frozen RIDs, with Manual/Auto policy, batch requests, rollback,
  NativeAOT-safe platform handling, UI status, tests, and production US/UK proof.
- Acceptance: release version and exact publish RID are embedded and reported; no arbitrary URL or shell execution;
  size/SHA/archive checks precede replacement; identity/DataDir/service state survive; update is independent from command
  interruption; new releases are discovered without Server restart/manual SCP; Linux process E2E and production Panel-led
  update pass without SSH/re-enrollment/manual Agent restart for the update hop.
- Result: Shared update contracts, strict SemVer, build-time version/RID stamping, persistent Agent state machine,
  same-origin streaming download, strict tar/zip extraction, staged self-test, Unix re-exec, Windows helper, rollback,
  Server GitHub release synchronization/cache, Manual/Auto desired version persistence, admin APIs and update UI are
  implemented. Release build passed with zero warnings; Shared 21/21, Server 100/100, and Agent 143/143 tests passed.
  An isolated Linux x64 NativeAOT process E2E passed `1.2.0 → 1.3.0`, preserving credentials and deleting `.previous`
  only after verified sync. GitHub Actions produced all eight Agent RIDs for `v0.3.0` and `v0.3.1`. The running US
  Server discovered and verified `v0.3.1` without restart or manual release copying. From the Admin API, UK Agent was
  requested to update `0.3.0 → 0.3.1`; it downloaded the Panel-cached linux-x64 asset, verified/staged/replaced and
  re-execed under the same PID with zero systemd restarts, retained Agent credentials, re-synced as `0.3.1`, reached
  `Succeeded`, and removed `.previous`. US Server was then deployed at `v0.3.1` and remained healthy.

## Phase 11 — Per-user proxy identity

> OP-005 remains complete and its update protocol is unchanged. Phase 11 corrects the earlier grant-only user model:
> a binding must own a real backend identity, and capability/usage claims must reflect observable backend behavior.

### HP-1100 — Clean routing and freeze per-user identity architecture
- Owner: architect (Sol)
- Depends on: OP-005
- Status: DONE
- Scope: remove Terra routing, reconcile completed update documentation, freeze credential encryption, desired-user,
  capability and cumulative-counter semantics without changing Agent identity or update contracts.
- Accept: only Sol owns non-mechanical implementation; Luna remains mechanical-only; docs and task state match OP-005.
- Result: Removed `.opencode/agents/terra.md` and every active Terra routing permission; retained historical task owner
  facts. Architect remains `home/gpt-5.6-sol`, Luna remains `home/gpt-5.6-luna`. ROADMAP no longer defers completed
  batch Agent updates and records Phase 11 boundaries.

### HP-1101 — Persist encrypted binding credentials and lifecycle
- Owner: architect (Sol)
- Depends on: HP-1100
- Status: DONE
- Scope: SQLite v9, explicit Server master-key configuration, authenticated encryption, create/revoke/rotate operations,
  revision changes and secret-safe Admin projections.
- Accept: no plaintext secret at rest or in logs; restart decrypts existing credentials; missing/wrong key fails safely.
- Result: SQLite v9 stores one AES-256-GCM credential per binding with binding identity as authenticated data. A
  persistent explicit master key is required; create, revoke, rotate, migration backfill and secret-free status APIs
  increment desired revisions. Revoked tombstones preserve retry ownership without retaining usable access.

### HP-1102 — Reconcile Xray multi-user desired state
- Owner: architect (Sol)
- Depends on: HP-1101
- Status: DONE
- Scope: additive shared desired users, eligibility filtering and deterministic Xray clients/stats/API configuration.
- Accept: one service runs multiple distinct UUIDs; removal/rotation affects only one user.
- Result: `ServiceDesiredState.Users` and reserved loopback `ControlPort` are additive. Xray deterministically renders
  one VLESS/REALITY/Vision client per eligible HyPanel user and enables official StatsService policy. Disabled,
  expired, limited, revoked and unbound users disappear without stopping the service.

### HP-1103 — Collect restart-safe Xray per-user traffic
- Owner: architect (Sol)
- Depends on: HP-1102
- Status: DONE
- Scope: official Xray StatsService collection and atomic cumulative baseline plus pending-batch persistence.
- Accept: per-user deltas survive Agent/backend restart and sync retries without double counting.
- Result: Xray's bounded fixed `api statsquery` collector maps stable non-secret email IDs to users. Agent usage state
  atomically persists cumulative baselines and pending batches; first traffic, sync retry, Agent restart and backend
  counter reset are covered by regression tests and production evidence.

### HP-1104 — Project per-user subscriptions and Admin UI
- Owner: architect (Sol)
- Depends on: HP-1101, HP-1102
- Status: DONE
- Scope: credential-based subscriptions, truthful support/status/usage projection and explicit rotate action.
- Accept: two users on one Xray service receive distinct UUIDs and secrets are not shown by default.
- Result: Subscription projection uses only the encrypted binding credential. User UI shows per-service Node/backend,
  credential status, capability truth and user usage, supports confirmation-gated rotation, never reveals the secret,
  and disables unsupported shared-secret grants.

### HP-1105 — Phase 11 integration and production acceptance
- Owner: architect (Sol)
- Depends on: HP-1101 through HP-1104
- Status: DONE
- Scope: focused/full tests, NativeAOT Agent/Server, Web build, real Xray clients, limits, rotation and restart checks.
- Accept: all Phase 11 ROADMAP exit criteria pass before Phase 12 Backend Release Management starts.
- Result: Release build passed with zero warnings; Shared 21/21, Server 101/101 and Agent 142/142 tests passed (264).
  Agent and Server NativeAOT publishes and Web build passed. On US Server + UK Agent, two users received different
  UUIDs for one Xray 26.3.27 service and both connected concurrently through Reality. Usage increased independently;
  limiting A removed only A while B stayed connected. Agent restart did not change totals. Rotating A invalidated its
  old UUID, enabled its new UUID, left B unchanged, and Server restart preserved/decrypted the new credential. Browser
  review passed at desktop/390px with truthful unsupported-backend states, no overflow and zero console messages.

## Phase 12 — Backend release management

### HP-1200 — Freeze controlled backend release sources and lifecycle
- Owner: architect (Sol)
- Depends on: HP-1105
- Status: DONE
- Scope: define official repositories, fixed RID asset mapping, archive extraction boundary, latest/desired/running model,
  manual/auto policy and per-service rollback without arbitrary URLs.
- Result: Official sources are fixed to HyNetwork/hysteria, XTLS/Xray-core, MetaCubeX/mihomo and SagerNet/sing-box.
  Server alone accesses GitHub, extracts exactly one expected executable and republishes a raw size/SHA-verified asset.
  `backend_version` remains desired; Agent runtime version remains running; catalog supplies latest. Updates reuse normal
  desired-state reconciliation and last-known-good rollback, never Agent commands or remote shell.

### HP-1201 — Implement upstream synchronization and runtime catalog
- Owner: architect (Sol)
- Depends on: HP-1200
- Status: DONE
- Scope: GitHub release metadata, controlled asset selection, bounded safe extraction, verified atomic cache and fallback.
- Result: Added independent 15-minute refresh for all four official repositories, exact eight-RID mappings, stable
  SemVer filtering, versioned runtime indexes and cache fallback. Raw/gzip/zip/tar.gz handling extracts exactly one
  expected executable under 256 MiB, validates final redirect hosts, computes raw size/SHA256 and atomically publishes.
  Artifact download is on demand, so routine metadata refresh does not consume hundreds of MB.

### HP-1202 — Implement backend update policy and Admin API
- Owner: architect (Sol)
- Depends on: HP-1201
- Status: DONE
- Scope: SQLite policy, current/latest/desired projection and manual update-to-latest with revision-safe mutation.
- Result: SQLite v10 adds per-service Manual/Auto policy. Existing `backend_version` is explicit Desired, runtime
  report is Current, and catalog is Latest. Manual API caches before changing desired revision; Auto performs the same
  preparation only after opt-in. Sync sends only desired artifacts actually used by enabled services.

### HP-1203 — Harden Agent update rollback and health verification
- Owner: architect (Sol)
- Depends on: HP-1202
- Status: DONE
- Scope: validate staged binary/config, restart only target service, bounded health window and restore prior binary/config.
- Result: Reconciliation now requires the new process to remain running and pass provider health after a bounded start
  window. Failure restores last-known-good desired metadata/config/binary and restarts only that service. Agent reports
  `backend_update_rolled_back`; Server transactionally restores desired version and increments revision once, preventing
  a retry loop.

### HP-1204 — Backend update UI and integration acceptance
- Owner: architect (Sol)
- Depends on: HP-1201 through HP-1203
- Status: DONE
- Scope: Service Detail versions/update action, full gates and real UK isolated update/rollback evidence.
- Result: Service UI shows Current/Desired/Latest, confirmation-gated update and Auto toggle. Release build and Web
  build passed; Shared 21/21, Server 106/106 and Agent 143/143 tests passed (270). Linux x64 and osx-x64 Agent/Server
  NativeAOT passed. Live source discovery found Hysteria2 2.12.3, Xray 26.3.27, Mihomo 1.19.31 and sing-box 1.14.1.
  On the online UK Agent, an isolated managed Hysteria2 service updated 2.12.2 → 2.12.3 from the Panel cache and
  converged Current/Desired 2.12.3. A deliberately failing 2.12.4 binary restored 2.12.3 and Server desired revision
  without affecting other services. Temporary acceptance resources were removed; desktop/390px UI had no overflow or
  console warnings/errors.

## Phase 13 — GHCR / Docker

### HP-1300 — Containerize the NativeAOT Server
- Owner: architect (Sol)
- Depends on: HP-1204
- Status: DONE
- Scope: `/data` layout, non-root minimal runtime, 8080 listener, health check and amd64/arm64 image contexts.
- Result: Official .NET 10 Alpine AOT SDK builds the Server in a throwaway stage; final runtime-deps image is 19.7 MB,
  runs as 10001:10001, contains only `HyPanel.Server` and `libe_sqlite3.so` under `/app`, has no dotnet/SDK/compiler,
  listens on 8080 and reports healthy. `/data` owns SQLite plus release caches and persisted an API-created Node across
  container replacement. Cross-linked macOS musl output was explicitly rejected after real OpenSSL startup failure.

### HP-1301 — Publish multi-architecture GHCR images
- Owner: architect (Sol)
- Depends on: HP-1300
- Status: DONE
- Scope: release workflow manifest list and `latest`, version and `sha-*` tags.
- Result: Native amd64 and `ubuntu-24.04-arm` jobs publish architecture tags, followed by a manifest job producing
  version, latest and sha tags. No QEMU compiler execution is used in CI. actionlint 1.7.7 passes.
- Acceptance: `v0.4.0` run 35169185811 succeeded. `ghcr.io/greepar/hypanel:0.4.0`, `latest` and `sha-165ad1e`
  resolve to OCI index `sha256:f9cc3901526f...` with linux/amd64 and linux/arm64 manifests.

### HP-1302 — Container runtime acceptance
- Owner: architect (Sol)
- Depends on: HP-1300, HP-1301
- Status: DONE
- Scope: actionlint, local image build, non-root/runtime-content inspection, health and volume persistence restart.
- Evidence: amd64 build/runtime/content/health/persistence and Compose validation pass. Local arm64 AOT cannot be
  accepted on this x86_64 host: QEMU `ilc` crashed and host clang lacks an aarch64 linker. The workflow intentionally
  uses a native arm64 runner; multi-arch manifest and GHCR pull must be verified by the next pushed release before
  Phase 13 is DONE. No production verification is claimed yet.
- Final evidence: GitHub native arm64 and amd64 jobs both succeeded. Both platform images were pulled from GHCR and
  started; internal `/health` returned `{"status":"ok"}`, HEALTHCHECK was healthy and runtime user was 10001:10001.
  The final image is approximately 19.7 MB and contains no dotnet executable, SDK or compiler.

## Phase 14 — Server self update

### HP-1400 — Freeze Server deployment/update model
- Owner: architect (Sol)
- Depends on: HP-1302
- Status: DONE
- Scope: separate bare-metal Server updater contract/state from Agent updates and Docker image availability.
- Result: Server update has independent build stamping, state, API and UI. Bare-metal Linux may replace only its fixed
  executable; Docker mode is read-only and never accesses the Docker daemon.

### HP-1401 — Implement bare-metal Server update
- Owner: architect (Sol)
- Depends on: HP-1400
- Status: DONE
- Scope: release discovery, RID selection, size/SHA, stage/self-test, replacement, service restart and rollback.
- Result: Official GitHub metadata selects exact Server RID archive, verifies declared size/SHA256, safely extracts one
  binary, executes exact version/RID self-test, preserves `.previous`, replaces and `exec`s. RestartPending enters a
  startup/stability verification window; a second verifying startup restores the previous binary.

### HP-1402 — Implement Docker image update status
- Owner: architect (Sol)
- Depends on: HP-1400
- Status: DONE
- Scope: detect container deployment and show pull/recreate guidance without Docker socket access.
- Result: `DOTNET_RUNNING_IN_CONTAINER`/deployment mode disables in-place replacement. API and Settings show current,
  latest and Docker guidance; update POST returns 409 and no Docker socket is mounted.

### HP-1403 — Server update UI and acceptance
- Owner: architect (Sol)
- Depends on: HP-1401, HP-1402
- Status: DONE
- Scope: Admin update surface, tests, NativeAOT and bare-metal/Docker lifecycle verification.
- Result: Shared 21/21, Server 108/108 and Agent 143/143 tests passed (272), Release/Web/NativeAOT and CI passed.
  US Server used the Admin API to self-update `0.4.0 → 0.4.1`: MainPID remained 301708 through `exec`, DB SHA stayed
  identical, health remained OK, state reached Succeeded and `.previous` was removed after stability verification.
  GHCR 0.4.1 Docker reported Docker mode, refused POST with 409 and remained healthy. Settings passed desktop/390px
  browser checks with zero overflow or console messages. Temporary acceptance resources were removed.

## Phase 15 — Global settings

### HP-1500 — Persist operational defaults and mirror
- Owner: architect (Sol)
- Depends on: HP-1403
- Status: DONE
- Scope: Agent/Backend default update policy and optional controlled HTTPS GitHub mirror.
- Result: SQLite v11 singleton settings default to Manual/Manual. New Nodes and Services inherit current defaults while
  existing rows remain unchanged. Mirror validation accepts only HTTPS base URLs without credentials/query/fragment;
  release clients rewrite only their fixed official GitHub URLs and validate final mirror/GitHub hosts.

### HP-1501 — Settings API and UI
- Owner: architect (Sol)
- Depends on: HP-1500
- Status: DONE
- Scope: top-level Settings navigation with operational defaults, release status, Server update and data information.
- Result: API/UI expose Agent release, four Backend releases, Server current/latest/deployment/status, data directory and
  database size. Production schema v11 read/write/restore passed; desktop/390px UI had no overflow or console messages.
  Release build, 272 tests and Web build pass. Temporary acceptance account was removed.

## Phase 16 — Cross-platform acceptance

### HP-1600 — Execute available real platform lifecycles
- Owner: architect (Sol)
- Depends on: HP-1501
- Status: PARTIAL — DEVICE GAPS RECORDED
- Result: Linux x64 systemd Agent/Server and amd64/arm64 Docker are real verified. macOS x64 and Alpine musl x64
  performed official release selection, size/SHA verification, safe extraction and exact self-test. Windows Service,
  macOS LaunchDaemon, Alpine OpenRC and OpenWrt procd remain explicitly pending because no reachable real device/init
  environment exists. `docs/CROSS_PLATFORM_ACCEPTANCE.md` contains executable acceptance checklists; none are falsely
  marked production verified.

## Phase 17 — Final architecture cleanup

### HP-1700 — Review core boundaries and operational risks
- Owner: architect (Sol)
- Depends on: HP-1600
- Status: DONE
- Result: Backend-specific behavior remains in providers, subscription projection, controlled release metadata and the
  Xray desired-user projection; reconciliation stays provider-driven. NativeAOT JSON paths use generated contexts,
  artifact downloads are bounded and verified, secret logging search is clean, SQLite uses explicit columns and sync
  sends only desired artifacts. One evidence-based optimization was applied: when revisions already match, sync no
  longer decrypts credentials or constructs a discarded desired payload. No cosmetic broad refactor was performed.

## Phase 18 — Linux Agent production hardening

### HP-1800 — Audit Linux Agent lifecycle and failure boundaries
- Owner: architect (Sol)
- Depends on: HP-1700
- Status: DONE
- Result: Audited Program/SyncWorker/reconciliation/process supervision/artifact management/updaters/state stores,
  installer/systemd, metrics and logs. Confirmed the production model is a dedicated unprivileged Agent user with
  Backend children in the same systemd cgroup; safe cross-restart process adoption is intentionally rejected.

### HP-1801 — Harden systemd install, reinstall and uninstall
- Owner: architect (Sol)
- Depends on: HP-1800
- Status: DONE
- Result: Added bounded systemd restart/start limiting, SIGTERM/timeout/control-group stop, NoNewPrivileges,
  PrivateTmp, owner-only umask and WorkingDirectory. Same-node repair preserves all state; token presence selects
  explicit re-enrollment. `--uninstall` preserves DataDir and `--purge` removes it. Ubuntu and AlmaLinux reinstall plus
  Ubuntu uninstall/reinstall passed with identity hashes unchanged and managed Backends stopped/restored.

### HP-1802 — Harden reconciliation, process recovery and state writes
- Owner: architect (Sol)
- Depends on: HP-1800
- Status: DONE
- Result: Added per-service failure isolation, crash restart/backoff states, real Unix SIGTERM, process-handle cleanup,
  no-op config preservation, content-addressed atomic config commit, restrictive service permissions, disk-space
  preflight and explicit critical/non-critical state corruption boundaries. Server now de-duplicates shared Backend
  artifacts before desired-state delivery. Focused regression tests cover these boundaries.

### HP-1803 — Linux glibc production acceptance
- Owner: architect (Sol)
- Depends on: HP-1801, HP-1802
- Status: DONE
- Result: Ubuntu 26.04 and AlmaLinux 10.2 x64 fresh/reinstall/uninstall, Agent/Backend crash recovery, graceful
  restart, four-service coexistence, Backend updates, config/write rollback, command-cache quarantine, identity failure,
  one-hour Panel outage, permission/secret audit, three-hour soak, real reboot, and published Agent update success/failure
  recovery passed. `linux-x64` and `linux-arm64` NativeAOT publish passed. Official Server and Agent now run `0.4.5`.
  ARM64 runtime hardware remains unavailable and is recorded as a platform evidence gap, not a Phase 18 blocker.

## Phase 19 — Hysteria2 certificate management

### HP-1900 — Certificate asset storage and API
- Owner: architect (Sol)
- Depends on: HP-1803
- Status: DONE
- Result: Added schema v12 certificate assets with unique name/fingerprint metadata and AES-256-GCM encrypted private
  keys under a certificate-specific AAD namespace. Admin list/upload/replace APIs validate PEM/key matching, validity
  interval, subject, SAN and SHA256 fingerprint. Normal responses never return certificate or private-key PEM; Server
  startup fails explicitly if encrypted certificate keys exist without the configured master key.

### HP-1901 — Hysteria2 binding and Agent TLS lifecycle
- Owner: architect (Sol)
- Depends on: HP-1900
- Status: DONE
- Result: Hysteria2 config binds `CertificateId` instead of Server filesystem paths. Authenticated desired state carries
  only the selected material. Agent writes fingerprint-addressed TLS assets under the service-private directory (cert
  0644, key 0600), renders relative paths, and strips PEM from generic state/metadata JSON. Replacement increments every
  referencing Node revision and uses existing config rollback/backoff behavior.

### HP-1902 — Certificate UI and production acceptance
- Owner: architect (Sol)
- Depends on: HP-1900, HP-1901
- Status: DONE — PRODUCTION ACCEPTED
- Result: Settings now lists/uploads/replaces certificates and HY2 service forms select a managed asset. Ubuntu/AlmaLinux
  acceptance verified encrypted SQLite storage, API secrecy, real HY2 startup, permission modes, successful rotation,
  mismatched-key rejection without revision change, read-only TLS write failure preserving the old PID/config/listener,
  and automatic convergence after permissions recovered. Official `v0.4.6` Release run `35426209085` published all
  NativeAOT assets and GHCR manifests. The US Server and UK Agent then self-updated from `0.4.5` without manual binary
  copying; Server migrated schema 11 → 12. Production accepted a managed-certificate HY2 listener, 0644/0600 TLS files,
  encrypted SQLite key storage and PEM-free API/state/logs. Rotation changed only the referencing Node revision 25 → 26
  and restarted that Service; a mismatched key returned 400 with revision and running Service unchanged.

## Phase 20 — Backup / Restore

### HP-2000 — Freeze disaster-recovery boundary and backup format
- Owner: architect (Sol)
- Depends on: HP-1902
- Status: DONE
- Scope: classify durable/rebuildable/excluded Server state; freeze the versioned manifest, SQLite snapshot, MasterKey
  fingerprint and compatibility rules.
- Result: Format v1 is an exact `manifest.json` + online `database.sqlite` gzip tar. The full SQLite logical state is
  durable; release/backend caches, logs, staging, updater state and deployment secrets are excluded. The manifest has
  an irreversible domain-separated MasterKey fingerprint only when encrypted rows exist.

### HP-2001 — Implement safe backup and restore core
- Owner: architect (Sol)
- Depends on: HP-2000
- Status: DONE
- Scope: online SQLite backup, bounded archive validation, encrypted-secret preflight, emergency snapshot, atomic
  replacement, rollback and controlled restart using one shared service for API and CLI.
- Result: WAL-safe online snapshots, bounded exact-entry archive parsing, SHA/integrity/schema/repository checks and
  complete proxy/certificate decrypt preflight run before mutation. API restore commits only during startup, after a
  pre-restore emergency archive; migration or validation failure restores the prior database.

### HP-2002 — Add Admin API, CLI and Settings data UI
- Owner: architect (Sol)
- Depends on: HP-2001
- Status: DONE
- Scope: Admin-only list/create/download/delete/validate/restore, disaster-recovery CLI, retention, secure download and
  explicit two-step restore confirmation.
- Result: Added Admin-only create/list/download/delete/validate/restore, basename confinement, 0600 archives, 0700
  directories, five-backup retention, no-store authenticated download, raw bounded upload, shared CLI commands and a
  Settings flow that requires validation plus literal `RESTORE` confirmation.

### HP-2003 — Backup / Restore production acceptance
- Owner: architect (Sol)
- Depends on: HP-2002
- Status: DONE — PRODUCTION ACCEPTED
- Scope: focused/full tests, Linux x64/arm64 Server NativeAOT, Web build, real state mutation/restore/reconnect/convergence
  and wrong-MasterKey rejection with the current database unchanged.
- Result: `v0.4.7` Release run `35428824474` published all NativeAOT assets and GHCR manifests; the US Server self-updated
  from `0.4.6`. A live schema-12 backup captured Node, running managed-certificate HY2, Xray, User, independent Xray
  credential, subscription token hash, 8,888,888 usage bytes and non-default settings. After recognizable mutations,
  API restore restarted the Server, created a 0600 emergency archive and restored every value; the UK Agent reconnected
  and both listeners remained converged. A wrong-MasterKey CLI preflight returned `master_key_mismatch` with the
  production DB SHA256 unchanged. Temporary User/Xray/usage/settings/backups were removed; the Phase 19R HY2 remains.

## Phase 21 — User-facing documentation

### HP-2100 — Rewrite project README and operator guidance
- Owner: architect (Sol)
- Depends on: HP-2003
- Status: DONE
- Scope: product overview, Docker/bare-metal Quick Start, enrollment, supported Services, update ownership, disaster
  recovery security, Linux troubleshooting, development and license guidance matching shipped behavior.
- Result: README is now a user-facing project homepage with a runnable GHCR Compose example, secret generation and
  first-run flow, honest current Backend profiles/capabilities, update ownership, managed-certificate guidance,
  Web/CLI disaster recovery, prominent archive + separately stored MasterKey warning, Linux commands, practical
  failure guidance, platform evidence limits, development commands and GPL-3.0 license link.

## Phase 22 — Backend capability audit

### HP-2200 — Audit official multi-user and traffic capabilities
- Owner: architect (Sol)
- Depends on: HP-2100
- Status: DONE — RESEARCH ONLY
- Scope: verify current official Hysteria2, Mihomo and sing-box capabilities and record only deterministic config,
  stable identity mapping and reliable Agent-side collection candidates; no implementation in this phase.
- Result: `docs/BACKEND_CAPABILITY_AUDIT.md` records official sources and a conservative matrix. Hysteria2 2.12.3 has
  deterministic userpass IDs plus authenticated per-ID cumulative traffic and is approved for implementation planning.
  Mihomo SS2022 has no stable per-user identity/stat contract and remains single-user. sing-box SS2022 has users but
  V2Ray user stats are build-conditional, so executable-level proof is required before claiming traffic capability.

### HP-2201 — Implement Hysteria2 per-user identity and traffic
- Owner: architect (Sol)
- Depends on: HP-2200
- Status: TODO
- Scope: deterministic `hypanel-{UserId:N}` userpass entries, independent encrypted passwords, loopback-only protected
  Traffic Stats API and cumulative counter ingestion through the existing idempotent usage pipeline.

### HP-2202 — Gate sing-box capability on official artifact behavior
- Owner: architect (Sol)
- Depends on: HP-2200
- Status: TODO — PROTOTYPE FIRST
- Scope: prove exact release artifact API inclusion, users[] identity, TCP/UDP counters and reset/restart behavior. If
  stats are unreliable, implement at most MultiUser and keep PerUserTraffic/TrafficStats false.

### HP-2203 — Keep Mihomo SS2022 single-user
- Owner: architect (Sol)
- Depends on: HP-2200
- Status: DECIDED — NO IMPLEMENTATION
- Scope: do not manufacture user isolation or traffic from shared passwords, logs, connections or estimates.

## Phase 23 — Product journey polish

### HP-2300 — Validate and polish the first-admin journey
- Owner: architect (Sol)
- Depends on: HP-2003, HP-2100
- Status: DONE
- Scope: fresh DB through Admin, Node/install, Service/certificate, User/grant/subscription/traffic and backup; preserve
  the visual system and fix only observed hierarchy, wording, empty-state and dangerous-action friction.
- Result: Fresh DB browser acceptance confirmed clear Overview/Node empty states, natural Node explanation, direct
  create→one-time install flow, Settings certificates and backup/validated restore confirmation. Production rollout
  exposed stale entry HTML after self-update; `/` now sends `no-store/no-cache` while hashed assets remain unchanged.
  Plain-HTTP enrollment failure now explains HTTPS and `X-Forwarded-Proto` instead of blaming form input. No broad UI
  redesign or internal identifier exposure was added.

## Phase 24 — Backend-driven service editor

### HP-2400 — Serve backend field definitions from the Server
- Owner: architect (Sol)
- Depends on: HP-2300
- Status: DONE
- Scope: expose the compile-time backend catalog over Admin API so the service editor renders fields from the Server and
  adding a backend never requires a frontend release.
- Result: `GET /api/admin/v1/backends` returns per-backend metadata (name/core/protocol/badge, latest version from the
  artifact catalog) plus typed field descriptors (text/password/number/select/certificate/fixed, required, secret,
  generate, defaults, options, min/max, section). Configuration keys match provider config JSON exactly. Covered by
  AOT source-generated JSON and 2 endpoint tests.

### HP-2401 — Generate secure random defaults incl. REALITY key pair
- Owner: architect (Sol)
- Depends on: HP-2400
- Status: DONE
- Scope: `POST /api/admin/v1/backends/{type}/defaults` returns fresh secrets; REALITY key pair must be valid without
  shipping an Xray binary on the Server.
- Result: Hysteria2 gets Base64Url auth/obfs passwords, Shadowsocks gets a 32-byte Base64 key, Xray gets a clamped
  X25519 REALITY key pair plus a hex Short ID. X25519 is a BCL-free RFC 7748 Montgomery ladder tested against the
  published vectors and cross-checked against the official `xray x25519 -i` output (derived public key matched).

### HP-2402 — Schema-driven service editor and defaults in the UI
- Owner: architect (Sol)
- Depends on: HP-2401
- Status: DONE (code + type/web/AOT gates) — UI browser acceptance pending
- Scope: render the service editor from the Server definitions, prefill generated secrets for new services, allow
  explicit regeneration, and keep existing behaviour/validation.
- Result: Preact editor now renders backend picker, listen and protocol fields from the definition, auto-requests
  generated defaults for new services, offers “重新生成默认密钥”, and builds payload/validation from the schema
  (including numeric bounds). `tsc`/Vite build, full 314 tests and Server linux-x64 NativeAOT publish pass.

### HP-2403 — Permit privileged Backend listen ports under systemd
- Owner: architect (Sol)
- Depends on: HP-1801, HP-2402
- Status: DONE (code) — existing-node repair acceptance pending
- Scope: retain the dedicated unprivileged Agent account while allowing managed Backend children to bind ports below
  1024 on production systemd nodes.
- Result: Production `sg-v` Xray logs proved TCP 443 failed with `bind: permission denied` while the identical config
  ran on 8443. The installer unit now bounds and supplies only `CAP_NET_BIND_SERVICE`; `NoNewPrivileges`, cgroup process
  ownership and all other restrictions remain. Existing installations must rerun the installer repair path to refresh
  the unit before moving a service back to 443.

### HP-2404 — Discover Agent public IPv4 for subscription fallback
- Owner: architect (Sol)
- Depends on: HP-2402
- Status: DONE (code + focused tests) — production rollout pending
- Scope: let an Agent discover and report its own outward-facing IPv4 so a newly granted Xray/Shadowsocks service can
  produce a usable subscription without requiring an immediate manual public-endpoint entry.
- Result: Agent queries the fixed HTTPS endpoint `https://4.qwq.lu` with a three-second timeout, 64-byte bound,
  canonical IPv4 validation, six-hour success cache and non-fatal failure cache. Schema 13 retains the latest valid
  address; authenticated sync connection IPv4 is a compatibility fallback for older Agents. Subscriptions prefer manual
  endpoints and otherwise use the observed IP plus `listenPort`. Hysteria2 remains explicit because its certificate/SNI
  cannot be inferred safely.
