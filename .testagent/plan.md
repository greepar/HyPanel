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
