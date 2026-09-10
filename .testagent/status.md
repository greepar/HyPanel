# Phase 7 Test Status

## Checklist -> tests

- Migration v6/table shape: `MigrateAsync_WhenRunTwice_AppliesEachMigrationOnceAndAddsCommandExpiryColumn`.
- Template uniqueness, CRUD, instantiation, and revision mutation: `Templates_InstantiateAndBatchEnabled_IncrementAffectedNodesOnce`.
- Template normalization/schema/redaction: `ServiceTemplateValidation_NormalizesNameAndRequiresSchemaVersionOne`.
- Batch same-Node single revision, multi-Node revisions, duplicate rejection, and ownership rollback: `Templates_InstantiateAndBatchEnabled_IncrementAffectedNodesOnce`.
- Health classification: `HealthSummaryBuild_ClassifiesOnlineDriftOfflineAndFailedServices`.
- Embedded index and hashed JS/CSS: `EmbeddedWebResources_ContainIndexAndReferencedAssets`.
- Asset path and MIME safety: `IsSafeAssetFileName_RejectsTraversalAndNonBasenames`, `GetContentType_ReturnsExplicitSafeMimeType`.

## Final verification target

- Release build: 0 warnings / 0 errors.
- Shared: 6/6.
- Server: 60/60.
- Agent: 111/111.
- Total: 177/177.
- Web production build and linux-x64 Server/Agent NativeAOT publish.
- US embedded Web HTTP/browser smoke and US+UK Phase 7 lifecycle evidence.

## Deferred by architecture

- Batch Agent binary update, backend channels, update signing, and mTLS/device keys require a separately frozen artifact trust and rollback protocol. They are not represented as arbitrary or payload-free Agent commands.
