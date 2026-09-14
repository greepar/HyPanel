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
