# Phase 4 test plan

1. Update existing Shared sync round-trip tests for usage fields and concrete values.
2. Update migration expectation to include version 4 and assert new tables.
3. Add focused `PasswordServiceTests` for valid, wrong, and malformed values.
4. Extend repository tests for user secret storage, session lifecycle, grants, token rotation, and usage idempotency/rollback.
5. Run Shared and Server test projects, then review assertions against production behavior.

---

# Phase 9 diagnostics test plan

1. Extend Shared serialization tests for `CollectServiceLogs` target service, health-check omission and successful `Output`.
2. Add focused Agent `ServiceLogCollectorTests` for byte-tail bounds, secret extraction, redaction, unmanaged service and empty buffer.
3. Extend SQLite tests for migration v7 columns, ownership, single active command, output persistence, forged-output rejection and retention.
4. Run focused projects, then full Release build/test and representative Agent + Server NativeAOT publishes.

---

# Agent self-update test plan

1. Add Shared SemVer tests and sync contract source-generation round trips.
2. Add build script verification for Version/FileVersion/InformationalVersion and all eight generated RID constants.
3. Extend ReleaseCatalog tests for refresh, cache fallback, exact RID selection and unsafe metadata.
4. Extend SQLite tests for migration v8, manual/auto policy, desired update identity and sync-reported lifecycle.
5. Add Agent updater tests for validation, streaming size/hash checks, tar/zip safety, staging, replacement rollback,
   no-downgrade, duplicate offer idempotency and persisted-state recovery.
6. Add endpoint tests for manual, retry, policy and batch update behavior.
7. Run a Linux process-level E2E harness with two stamped Agent binaries and a local fake Panel.
8. Run focused tests after each phase, then full build/test, Web build and representative Linux NativeAOT publish.

---

## Phase 19 test plan

- Migrate `Hysteria2ProviderTests` to certificateId plus `TlsCertificateAsset`; assert fingerprint paths.
- Extend reconciliation infrastructure tests for TLS file modes and metadata redaction.
- Add certificate validation/protector tests with generated self-signed certificates.
- Add repository integration tests for encrypted storage and replacement revision increments.
- Run focused Agent/Server tests, then full solution tests and review assertions.
