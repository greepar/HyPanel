# Phase 4 test plan

1. Update existing Shared sync round-trip tests for usage fields and concrete values.
2. Update migration expectation to include version 4 and assert new tables.
3. Add focused `PasswordServiceTests` for valid, wrong, and malformed values.
4. Extend repository tests for user secret storage, session lifecycle, grants, token rotation, and usage idempotency/rollback.
5. Run Shared and Server test projects, then review assertions against production behavior.
