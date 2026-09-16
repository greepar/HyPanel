# Phase 4 test research

Scope: `HyPanel.Shared` usage serialization and `HyPanel.Server` user/session/grant/usage persistence.

Conventions: SDK-style .NET 10, MSTest, temporary SQLite database fixture in
`SqliteServerRepositoryTests`, explicit assertions, no external network.

Acceptance checklist:

- Usage batch and acknowledgement serialize through source-generated JSON.
- Migration v4 is idempotent and creates all Phase 4 tables.
- PBKDF2 verifies the correct password and rejects wrong/malformed records.
- Passwords, session tokens, and subscription tokens are not persisted in plaintext.
- Sessions honor expiry, revocation, disabled users, and user expiration.
- Grants are idempotent and reject missing user/service IDs.
- Subscription token rotation invalidates the old hash and stores only the new hash.
- Replaying `(AgentId, BatchId)` does not add usage twice.
- Wrong Agent/service ownership or absent user binding rolls back the whole sync.
- Usage arithmetic overflow rolls back the whole sync.

---

# Phase 9 diagnostics test research

Scope: additive `CollectServiceLogs` command contract, Agent bounded/redacted collection, Server migration and persistence.

Risks to prove: string-enum JSON compatibility for the new command, optional field defaults, UTF-8 byte caps without
splitting surrogate pairs, exact secret redaction, unmanaged-service refusal, empty-buffer success, Node/Agent
ownership, output accepted only for successful log commands, duplicate active command prevention, terminal retention.

---

# Agent self-update test research

Scope: Shared version/update contracts, release version/RID stamping, Server release cache and Node update policy,
Agent persisted updater state/download/archive/replacement/recovery, admin update APIs, and Linux end-to-end restart.

Conventions: SDK-style .NET 10 NativeAOT, MSTest, source-generated System.Text.Json, temporary directories and SQLite,
no shell-based updater core, no ordinary AgentCommand for self-update.

Acceptance checklist:

- Release version and exact publish RID are embedded in the Agent build and reported during sync.
- Stable/prerelease semantic versions compare centrally without downgrades.
- Exactly one of the eight frozen RIDs selects an Agent release asset.
- Manual and auto policies keep current/latest/desired versions distinct and persist across Server restarts.
- Release discovery refreshes from GitHub into a local cache without making Agent sync depend on GitHub availability.
- Update offers contain validated metadata, no arbitrary URL, and are idempotent by update ID.
- Agent downloads same-origin assets as a stream and rejects redirects, unsafe names, wrong RID, size, and SHA-256.
- Tar/zip extraction rejects traversal, links, duplicate entries, and unsupported files.
- Staging never changes credentials, desired/service/backend/usage/command state or appsettings.
- Replacement preserves the old executable on failure and retains a previous binary until post-update sync verification.
- Interrupted download/staging/apply/restart states recover deterministically without an update loop.
- Linux harness runs an old Agent through download, replacement, restart and a successful new-version sync.
- Windows helper planning/path/token validation is testable on non-Windows CI.
