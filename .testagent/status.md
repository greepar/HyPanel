# Phase 7 Test Status

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
