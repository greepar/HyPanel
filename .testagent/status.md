# Phase 7 Test Status

# Phase 20 Test Status

- Core backup/restore tests: PASS, 17/17. Covers WAL snapshot data, manifest/list metadata, retention, active download,
  traversal, oversized/invalid/incomplete/duplicate/SHA-corrupt archives, newer schema, MasterKey mismatch, corrupt
  credential/certificate key, same-schema restore, schema 11 migration and migration-failure emergency rollback.
- Full tests: PASS, Shared 21 + Server 133 + Agent 151 = 305.
- Release solution build: PASS, 0 warnings / 0 errors.
- Web production build: PASS.
- Server NativeAOT: PASS for linux-x64 and linux-arm64.
- `git diff --check`: PASS.
- Production mutation/restore acceptance: pending formal release rollout.

---

## Checklist -> tests

- Migration v6/table shape: `MigrateAsync_WhenRunTwice_AppliesEachMigrationOnceAndAddsCommandExpiryColumn`.
- Template uniqueness, CRUD, instantiation, and revision mutation: `Templates_InstantiateAndBatchEnabled_IncrementAffectedNodesOnce`.
- Template normalization/schema/redaction: `ServiceTemplateValidation_NormalizesNameAndRequiresSchemaVersionOne`.
- Batch same-Node single revision, multi-Node revisions, duplicate rejection, and ownership rollback: `Templates_InstantiateAndBatchEnabled_IncrementAffectedNodesOnce`.
- Health classification: `HealthSummaryBuild_ClassifiesOnlineDriftOfflineAndFailedServices`.
- Embedded index and hashed JS/CSS: `EmbeddedWebResources_ContainIndexAndReferencedAssets`.
- Asset path and MIME safety: `IsSafeAssetFileName_RejectsTraversalAndNonBasenames`, `GetContentType_ReturnsExplicitSafeMimeType`.

## Final verification

- Release build: PASS, 0 warnings / 0 errors.
- Shared: PASS, 6/6.
- Server: PASS, 60/60.
- Agent: PASS, 111/111.
- Total: PASS, 177/177.
- Web production build: PASS.
- Server and Agent linux-x64 NativeAOT publish: PASS.
- US template/batch/rollback E2E and embedded Web HTTP/browser smoke: PASS; browser console has 0 errors and 0 warnings.
- UK Agent revision, Hysteria2 listener, isolation, and cleanup checks: PASS.
- GitHub Release `v0.2.0`: PASS; 17 assets, 8 Agent RIDs, 4 Server RIDs, manifest version `0.2.0`, and SHA256SUMS.

## Deferred by architecture

- Batch Agent binary update, backend channels, update signing, and mTLS/device keys require a separately frozen artifact trust and rollback protocol. They are not represented as arbitrary or payload-free Agent commands.

---

# Phase 9 Test Status

## Checklist -> tests

- `CollectServiceLogs` carries `TargetServiceId`: `CollectServiceLogsCommand_RoundTripsTargetServiceIdAndStringEnum`.
- `RunHealthCheck` omits target: `RunHealthCheckCommand_OmitsTargetServiceId`.
- Successful `Output` round-trips: `SuccessfulCommandOutput_RoundTripsOptionalPayload`.
- UTF-8 byte cap without splitting surrogate pairs: `TailUtf8_UnicodeInput_ReturnsValidNewestContentWithinByteLimit`, `TailUtf8_ContentWithinLimit_IsReturnedUnchanged`.
- Only named secrets extracted, longest first: `ReadSecrets_NestedConfiguration_ReturnsOnlyNamedSecretsLongestFirst`, `ReadSecrets_ShortOrUnnamedValues_AreIgnored`.
- Unmanaged service refused without filesystem read: `CollectAsync_UnmanagedService_ReturnsNullWithoutReadingFilesystem`.
- Applied secret redacted from real captured output: `CollectAsync_ManagedProcessOutput_RedactsAppliedSecret`.
- Empty buffer is success: `CollectAsync_EmptyLogBuffer_ReturnsEmptyStringNotError`.
- Migration v7 columns applied once: `MigrateAsync_WhenRunTwice_AppliesEachMigrationOnceAndAddsCommandExpiryColumn`.
- Ownership + single active command + health-check separation: `Diagnostics_CommandCreation_EnforcesOwnershipAndSingleActiveCommand`.
- Output persisted; forged health-check output rejected transactionally: `Diagnostics_SyncPersistsBoundedOutput_AndRejectsForgedOutputOnHealthCheck`.
- Terminal retention capped at 20: `Diagnostics_TerminalRetention_KeepsOnlyTwentyMostRecent`.

## Review findings and fixes

- Correctness: `RecordCommandResultAsync` originally discarded a forged `Output` while still accepting the sync.
  Fixed to return `false` and roll back the whole transaction; the test asserts the health-check command stays `Pending`.
- Consistency: `GetActiveHealthCheckCommandsAsync` delegated to the unfiltered active-command query. Fixed to filter
  `RunHealthCheck`.
- Retention: terminal pruning excludes `Pending`/`Running`; expired unfinished commands are pruned separately.

## Final verification

- Release build: PASS, 0 warnings / 0 errors.
- Shared: PASS, 9/9. Server: PASS, 63/63. Agent: PASS, 118/118. Total: PASS, 190/190.
- Agent and Server osx-x64 NativeAOT publish: PASS.
- Browser diagnostics review: PASS; all states render, collection disables while active, 390px no overflow, 0 console errors.

---

# Acceptance Remediation Final Status

## Regression coverage

- AR-001 diagnostics safety/lifecycle: PASS. Capture/output bounds, complete UTF-8 entries, secret redaction,
  unmanaged/empty collection, expiry, transactional output validation, physical 20-record retention and service
  deletion with diagnostics are covered.
- AR-002 frontend correctness/accessibility: PASS. Load/retry, node cancellation, redacted-secret preservation, UTC
  expiration, restored/bootstrap session validation, canonical cache cleanup, keyboard format selection, QR sync,
  progressbar semantics and synchronous User/Admin token-rotation exclusion were reviewed.
- AR-003 telemetry semantics: PASS. CPU/memory/uptime/network invariants, host/container uptime, malformed/missing
  snapshot degradation and Admin rendering were verified.

## Final gate — 2026-09-12

- Clean Web install: PASS; 0 vulnerabilities. Production build: PASS.
- Release solution build: PASS, 0 warnings / 0 errors.
- Shared: PASS, 9/9. Server: PASS, 76/76. Agent: PASS, 121/121. Total: PASS, 206/206.
- Agent and Server osx-x64 NativeAOT publish: PASS. macOS outputs are ad-hoc signed; public distribution still
  requires the documented Developer ID signing step.
- `git diff --check`: PASS.
- Embedded-Web browser acceptance: PASS using the current hashed production bundle. Account and Bootstrap sessions,
  role-tamper recovery, subscriptions, User/Admin rotation single-flight behavior, telemetry and Light/Dark/System
  themes passed. Subscription modal and Admin pages have no horizontal overflow at 390px.
- Fresh-tab browser console: PASS, 0 errors / 0 warnings.

## US operational verification — 2026-09-12

- Lucky HTTPS ingress: PASS at `https://us.greepar.uk`; Kestrel remains loopback-only on
  `http://127.0.0.1:5291`, with direct 8443 TLS disabled.
- Trusted forwarded headers: PASS. Only one forwarded loopback proxy hop can supply forwarded host/protocol/address headers;
  public node install commands preserve the external HTTPS origin.
- Simplified navigation: PASS. One HyPanel wordmark remains in the top bar; duplicate sidebar branding and
  single-character navigation marks are removed.
- One-line installers: PASS. Public UI emits `curl -Ls https://<panel>/i/<6-digits> | bash` for Linux/macOS and
  `irm https://<panel>/i/<6-digits> | iex` for Windows. Six-digit codes are 15-minute, single-redemption in-memory
  aliases protected by per-IP rate limiting; they resolve to the existing 256-bit enrollment token. Bootstrap
  responses remain platform-bound and no-store/no-referrer.
- US Server linux-x64 NativeAOT publish/deployment: PASS. Public health and both bootstrap payloads: PASS. Browser

---

# Agent self-update test quality review

- Scope: Shared SemVer/contracts, Server release cache/policy/persistence, Agent updater state/download/archive/
  replacement/recovery, and Linux process E2E.
- Assertions cover equality, negative/boolean decisions, exact exception/error codes, persisted state transitions,
  filesystem side effects, collection/RID selection, credential byte preservation, and process-level version change.
- No generated update test is assertion-free or null-only. Exception tests assert exact safe error codes rather than
  accepting any failure.
- Pseudo-mutation review added explicit survivors for same-version/downgrade decisions, duplicate offer idempotency,
  restart-pending recovery, stable-only Auto policy, manual same/downgrade rejection, all eight RID selection, zip/tar
  traversal/duplicates/links, and Windows helper sibling paths.
- Linux x64 NativeAOT process E2E ran OLD `1.2.0` to NEW `1.3.0` through a loopback Panel and real archive. It verified
  download, size/SHA, extraction, staged self-test, executable replacement, `execv`, new-version sync, unchanged
  credentials, `Succeeded` persisted state, and post-sync deletion of `.previous`.
- Residual platform risk: Windows locked-executable replacement cannot execute on Linux CI. Its path, parent-process,
  update identity/state and rollback planning are isolated and tested; production Windows integration remains a
  platform-specific acceptance item when a Windows node is available.
- Production acceptance: US Server cached `v0.3.1` from GitHub without restart; the Admin API requested UK Agent
  `0.3.0 → 0.3.1`. The Agent retained its systemd PID, reported zero restarts, matched the released binary SHA,
  preserved credentials, completed a verified sync, persisted `Succeeded`, and removed `.previous`.
  console: 0 errors / 0 warnings. Server warning log: empty.
- Agent release 0.2.1: PASS; all eight frozen NativeAOT RID assets and SHA256SUMS deployed. UK linux-x64 Agent matches
  the release hash, is online, and sustained at least ten successful syncs in 90 seconds with no Agent/Server warnings.
- Same-node re-enrollment: PASS; existing Agent identity is retained while its secret and observations are atomically
  rotated/reset. Installer rollback preserves prior binary and state on failed enrollment.
- Offline runtime presentation: PASS; the historical offline `uk-agent` service renders `未知`, not stale `运行中`.

---

## Phase 19 certificate test status

- Certificate validation: matching PEM/key accepted; mismatch, expired, and not-yet-valid rejected.
- Storage: private key ciphertext differs from plaintext and decrypts with the persistent master key; missing key is fatal.
- Agent: fingerprint paths, cert 0644/key 0600, PEM exclusion from state/metadata and bounded TLS version retention verified.
- Production acceptance: real HY2 start, rotation, invalid-key rejection, read-only write preservation and automatic retry passed.
- Final gate: Shared 21, Server 116, Agent 151; Release/Web/NativeAOT x64+arm64 passed.
