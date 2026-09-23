using HyPanel.Server.Persistence;
using HyPanel.Server.Endpoints;
using HyPanel.Server.Security;
using HyPanel.Shared.Contracts;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Server.Tests.Persistence;

[TestClass]
public sealed class SqliteServerRepositoryTests
{
    [TestMethod]
    public async Task MigrateAsync_WhenRunTwice_AppliesEachMigrationOnceAndAddsCommandExpiryColumn()
    {
        await using var fixture = await TestDatabase.CreateAsync();

        await fixture.Migrations.MigrateAsync(CancellationToken.None);

        await using var connection = await fixture.OpenConnectionAsync();
        var appliedMigrations = new List<(long Version, long Count)>();
        {
            await using var migrationCommand = connection.CreateCommand();
            migrationCommand.CommandText =
                "SELECT version, COUNT(*) FROM schema_migrations GROUP BY version ORDER BY version;";
            await using var migrations = await migrationCommand.ExecuteReaderAsync();
            while (await migrations.ReadAsync())
            {
                appliedMigrations.Add((migrations.GetInt64(0), migrations.GetInt64(1)));
            }
        }

        CollectionAssert.AreEqual(
            new List<(long Version, long Count)> { (1L, 1L), (2L, 1L), (3L, 1L), (4L, 1L), (5L, 1L), (6L, 1L), (7L, 1L), (8L, 1L), (9L, 1L), (10L, 1L), (11L, 1L), (12L, 1L), (13L, 1L), (14L, 1L), (15L, 1L), (16L, 1L), (17L, 1L), (18L, 1L), (19L, 1L) },
            appliedMigrations);

        var names = new List<string>();
        {
            await using var columnsCommand = connection.CreateCommand();
            columnsCommand.CommandText = "PRAGMA table_info(agent_commands);";
            await using var columns = await columnsCommand.ExecuteReaderAsync();
            while (await columns.ReadAsync())
            {
                names.Add(columns.GetString(1));
            }
        }

        CollectionAssert.Contains(names, "expires_at_utc");
        CollectionAssert.Contains(names, "target_service_id");
        CollectionAssert.Contains(names, "output");
        CollectionAssert.AreEquivalent(new[]
            {
                "id", "name", "normalized_name", "backend_type", "backend_version", "config_schema_version",
                "config_json", "created_at_utc", "updated_at_utc"
            },
            await ReadColumnNamesAsync(connection, "service_templates"));
        CollectionAssert.Contains(await ReadColumnNamesAsync(connection, "user_service_credentials"), "ciphertext");
        CollectionAssert.Contains(await ReadColumnNamesAsync(connection, "certificates"), "key_ciphertext");
        CollectionAssert.Contains(await ReadColumnNamesAsync(connection, "agents"), "public_ipv4");
    }

    [TestMethod]
    public async Task Certificates_PrivateKeyIsEncryptedAndReplacementIncrementsReferencingNode()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var id = Guid.NewGuid();
        var node = Guid.NewGuid();
        var unrelatedNode = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(node, "cert-node", CancellationToken.None);
        await fixture.Repository.CreateNodeAsync(unrelatedNode, "unrelated-node", CancellationToken.None);
        var value = new CertificateRecord(id, "example", "Upload", "CERT", fixture.Time.GetUtcNow(), fixture.Time.GetUtcNow(),
            fixture.Time.GetUtcNow().AddDays(30), new string('a', 64), "CN=example", "DNS:example.com", 0, "PRIVATE");
        Assert.IsTrue(await fixture.Repository.CreateCertificateAsync(value, CancellationToken.None));
        await using (var c = await fixture.OpenConnectionAsync())
        await using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT key_ciphertext FROM certificates WHERE id=@id;";
            cmd.Parameters.AddWithValue("@id", id.ToString("D"));
            var encrypted = (byte[])(await cmd.ExecuteScalarAsync())!;
            Assert.AreNotEqual("PRIVATE", Encoding.UTF8.GetString(encrypted));
        }
        var loaded = await fixture.Repository.GetCertificateWithKeyAsync(id, CancellationToken.None);
        Assert.AreEqual("PRIVATE", loaded!.PrivateKeyPem);
        var now = fixture.Time.GetUtcNow();
        var config = JsonSerializer.Serialize(new { certificateId = id });
        await fixture.Repository.CreateServiceAsync(new ServiceInstanceRecord(Guid.NewGuid(), node, "hy2", "hysteria2",
            "2.12.3", true, 1, config, now, now), CancellationToken.None);
        var before = (await fixture.Repository.GetNodeAsync(node, CancellationToken.None))!.DesiredRevision;
        var unrelatedBefore = (await fixture.Repository.GetNodeAsync(unrelatedNode, CancellationToken.None))!.DesiredRevision;
        Assert.IsTrue(await fixture.Repository.ReplaceCertificateAsync(value with { Fingerprint = new string('b', 64) },
            CancellationToken.None));
        Assert.AreEqual(before + 1,
            (await fixture.Repository.GetNodeAsync(node, CancellationToken.None))!.DesiredRevision);
        Assert.AreEqual(unrelatedBefore,
            (await fixture.Repository.GetNodeAsync(unrelatedNode, CancellationToken.None))!.DesiredRevision);
    }

    [TestMethod]
    public async Task InitializeCertificatesAsync_EncryptedCertificateWithoutMasterKey_FailsExplicitly()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var id = Guid.NewGuid();
        var now = fixture.Time.GetUtcNow();
        Assert.IsTrue(await fixture.Repository.CreateCertificateAsync(new CertificateRecord(id, "cert", "Upload", "CERT", now,
            now, now.AddDays(1), new string('c', 64), "CN=test", "", 0, "PRIVATE"), CancellationToken.None));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:HyPanel"] = fixture.ConnectionString
        }).Build();
        var repository = new SqliteServerRepository(new SqliteConnectionFactory(configuration), fixture.Time,
            new ProxyCredentialProtector(configuration));

        var exception = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            repository.InitializeCertificatesAsync(CancellationToken.None));

        StringAssert.Contains(exception.Message, "MasterKey");
    }

    [TestMethod]
    public async Task BatchEnabled_IncrementsAffectedNodesOnce()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeOne = Guid.NewGuid();
        var nodeTwo = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(nodeOne, "one", CancellationToken.None);
        await fixture.Repository.CreateNodeAsync(nodeTwo, "two", CancellationToken.None);
        var first = await fixture.Repository.CreateServiceAsync(CreateService(nodeOne, Guid.NewGuid(), "first"),
            CancellationToken.None);
        var second = await fixture.Repository.CreateServiceAsync(CreateService(nodeTwo, Guid.NewGuid(), "second"),
            CancellationToken.None);
        var third = await fixture.Repository.CreateServiceAsync(CreateService(nodeOne, Guid.NewGuid(), "third"),
            CancellationToken.None);

        var result = await fixture.Repository.SetServicesEnabledBatchAsync(new[]
        {
            new BatchServiceEnabledItemRecord(nodeOne, first.Service!.Id, false),
            new BatchServiceEnabledItemRecord(nodeOne, third.Service!.Id, false),
            new BatchServiceEnabledItemRecord(nodeTwo, second.Service!.Id, false),
        }, CancellationToken.None);
        Assert.IsNotNull(result);
        CollectionAssert.AreEqual(new[] { nodeOne, nodeTwo }.Order().ToArray(), result.Select(x => x.NodeId).ToArray());
        CollectionAssert.AreEquivalent(new[] { 3L, 2L }, result.Select(x => x.Revision).ToArray());

        var before = await fixture.Repository.GetNodeAsync(nodeOne, CancellationToken.None);
        Assert.IsNull(await fixture.Repository.SetServicesEnabledBatchAsync(new[]
        {
            new BatchServiceEnabledItemRecord(nodeOne, second.Service.Id, true)
        }, CancellationToken.None));
        Assert.AreEqual(before!.DesiredRevision,
            (await fixture.Repository.GetNodeAsync(nodeOne, CancellationToken.None))!.DesiredRevision);

        Assert.IsNull(await fixture.Repository.SetServicesEnabledBatchAsync(new[]
        {
            new BatchServiceEnabledItemRecord(nodeOne, first.Service!.Id, true),
            new BatchServiceEnabledItemRecord(nodeOne, first.Service!.Id, false)
        }, CancellationToken.None));
        Assert.IsFalse((await fixture.Repository.GetServicesForNodeAsync(nodeOne, CancellationToken.None))
            .Single(item => item.Service.Id == first.Service!.Id).Service.Enabled);
    }

    [TestMethod]
    public async Task AgentUpdatePolicyAndRequest_PersistIndependentlyFromServiceRevision()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(nodeId, "update node", CancellationToken.None);
        await fixture.Repository.CreateEnrollmentTokenAsync(Guid.NewGuid(), nodeId, "update-token",
            fixture.Time.GetUtcNow().AddMinutes(15), CancellationToken.None);
        var enrolled = await fixture.Repository.TryConsumeEnrollmentTokenAndCreateAgentAsync("update-token",
            Guid.NewGuid(), "agent-secret", "1.1.0", "linux-x64", CancellationToken.None);
        Assert.IsNotNull(enrolled);

        Assert.IsTrue(await fixture.Repository.SetAgentUpdatePolicyAsync(nodeId, "Auto", CancellationToken.None));
        var updateId = Guid.NewGuid();
        Assert.IsTrue(await fixture.Repository.RequestAgentUpdateAsync(nodeId, "1.3.0", updateId,
            CancellationToken.None));

        var observation = (await fixture.Repository.GetNodeObservationsAsync(CancellationToken.None)).Single();
        Assert.AreEqual("Auto", observation.AgentUpdatePolicy);
        Assert.AreEqual("1.3.0", observation.DesiredAgentVersion);
        Assert.AreEqual(updateId, observation.AgentUpdateId);
        Assert.AreEqual(0L, observation.DesiredRevision);
    }

    [TestMethod]
    public async Task AgentUpdateReport_WithMatchingUpdateId_PersistsFailureForPanelObservation()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(nodeId, "failed update", CancellationToken.None);
        await fixture.Repository.CreateEnrollmentTokenAsync(Guid.NewGuid(), nodeId, "failure-token",
            fixture.Time.GetUtcNow().AddMinutes(15), CancellationToken.None);
        var enrolled = await fixture.Repository.TryConsumeEnrollmentTokenAndCreateAgentAsync("failure-token",
            Guid.NewGuid(), "agent-secret", "1.1.0", "linux-x64", CancellationToken.None);
        var updateId = Guid.NewGuid();
        await fixture.Repository.RequestAgentUpdateAsync(nodeId, "1.3.0", updateId, CancellationToken.None);

        await fixture.Repository.RecordAgentUpdateReportAsync(enrolled!.AgentId,
            new AgentUpdateReport(updateId, AgentUpdateStatus.Failed, "1.3.0", "linux-x64",
                fixture.Time.GetUtcNow(), "1.1.0", "sha256_mismatch"), CancellationToken.None);

        var observation = (await fixture.Repository.GetNodeObservationsAsync(CancellationToken.None)).Single();
        Assert.AreEqual("Failed", observation.UpdateStatus);
        Assert.AreEqual("1.3.0", observation.UpdateTargetVersion);
        Assert.AreEqual("sha256_mismatch", observation.UpdateError);
    }

    [TestMethod]
    public void MergeRedactedSecrets_PreservesMatchingSecretsButAllowsExplicitRemoval()
    {
        const string existing = "{\"authPassword\":\"keep-auth\",\"obfsPassword\":\"keep-obfs\",\"nested\":{\"realityPrivateKey\":\"keep-key\"}}";
        const string submitted = "{\"authPassword\":\"[REDACTED]\",\"nested\":{\"realityPrivateKey\":\"[REDACTED]\"}}";

        Assert.IsTrue(AdminServicesEndpoints.TryMergeRedactedSecrets(existing, submitted, out var merged));
        using var document = JsonDocument.Parse(merged);
        Assert.AreEqual("keep-auth", document.RootElement.GetProperty("authPassword").GetString());
        Assert.AreEqual("keep-key", document.RootElement.GetProperty("nested").GetProperty("realityPrivateKey").GetString());
        Assert.IsFalse(document.RootElement.TryGetProperty("obfsPassword", out _));
        Assert.IsFalse(merged.Contains("[REDACTED]", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TrafficLimitValidation_RejectsValuesThatJavaScriptCannotRepresentExactly()
    {
        Assert.IsTrue(UserEndpoints.IsValidTrafficLimit(null));
        Assert.IsTrue(UserEndpoints.IsValidTrafficLimit(UserEndpoints.MaximumBrowserSafeBytes));
        Assert.IsFalse(UserEndpoints.IsValidTrafficLimit(-1));
        Assert.IsFalse(UserEndpoints.IsValidTrafficLimit(UserEndpoints.MaximumBrowserSafeBytes + 1));
    }

    [TestMethod]
    public async Task NodeObservations_ReturnLatestMetricSnapshot_ForAdminTelemetry()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "telemetry-token", "telemetry-secret");
        var metrics = new NodeMetrics(fixture.Time.GetUtcNow(), 3600, 12.5, 16_000, 4_000, 100_000, 25_000, 700, 800);
        var snapshotJson = JsonSerializer.Serialize(metrics,
            HyPanel.Shared.Serialization.HyPanelJsonSerializerContext.Default.NodeMetrics);

        Assert.IsTrue(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "1.0.0", "linux-x64", 0, snapshotJson,
            [], [], CancellationToken.None));

        var observation = (await fixture.Repository.GetNodeObservationsAsync(CancellationToken.None))
            .Single(item => item.Id == nodeId);
        var readBack = AdminObservationEndpoints.TryReadMetrics(observation.LatestMetricSnapshotJson);

        Assert.IsNotNull(readBack);
        Assert.AreEqual(metrics.CpuUsagePercent, readBack.CpuUsagePercent);
        Assert.AreEqual(metrics.MemoryTotalBytes, readBack.MemoryTotalBytes);
        Assert.AreEqual(metrics.DiskAvailableBytes, readBack.DiskAvailableBytes);
        Assert.AreEqual(metrics.NetworkUploadBytes, readBack.NetworkUploadBytes);
        Assert.AreEqual(metrics.ObservedAt, readBack.ObservedAt);
    }

    [TestMethod]
    public void TryReadMetrics_MissingOrMalformedSnapshot_ReturnsNullWithoutThrowing()
    {
        Assert.IsNull(AdminObservationEndpoints.TryReadMetrics(null));
        Assert.IsNull(AdminObservationEndpoints.TryReadMetrics("   "));
        Assert.IsNull(AdminObservationEndpoints.TryReadMetrics("{ not json"));
        var now = DateTimeOffset.Parse("2026-09-11T12:00:00+00:00");
        var invalid = JsonSerializer.Serialize(new NodeMetrics(now, 1, 10, 10, 11, 10, 5, 0, 0),
            HyPanel.Shared.Serialization.HyPanelJsonSerializerContext.Default.NodeMetrics);
        Assert.IsNull(AdminObservationEndpoints.TryReadMetrics(invalid, now));
    }

    [TestMethod]
    public async Task HealthSummaryBuild_ClassifiesOnlineDriftOfflineAndFailedServices()
    {
        var now = new DateTimeOffset(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);
        var onlineNode = Guid.NewGuid();
        var offlineNode = Guid.NewGuid();
        var nodes = new[]
        {
            new NodeObservationRecord(onlineNode, "online", 4, Guid.NewGuid(), now.AddSeconds(-5), "1", "linux", 4),
            new NodeObservationRecord(offlineNode, "offline", 3, Guid.NewGuid(), now.AddMinutes(-1), "1", "linux", 2),
            new NodeObservationRecord(Guid.NewGuid(), "not enrolled", 0, null, null, null, null, null)
        };
        var services = new[]
        {
            new HealthServiceRecord(Guid.NewGuid(), onlineNode, "running", (int)ServiceRuntimeStatus.Running, null,
                null),
            new HealthServiceRecord(Guid.NewGuid(), offlineNode, "failed", (int)ServiceRuntimeStatus.Failed,
                "badConfig", "safe message"),
            new HealthServiceRecord(Guid.NewGuid(), onlineNode, "unknown", null, null, null)
        };

        var result = AdminHealthSummaryEndpoints.Build(now, nodes, services);

        Assert.AreEqual(now, result.ObservedAtUtc);
        Assert.AreEqual(3, result.Counts.NodesTotal);
        Assert.AreEqual(1, result.Counts.NodesOnline);
        Assert.AreEqual(1, result.Counts.NodesDrifted);
        Assert.AreEqual(3, result.Counts.ServicesTotal);
        Assert.AreEqual(1, result.Counts.ServicesRunning);
        Assert.AreEqual(1, result.Counts.ServicesFailed);
        Assert.AreEqual(1, result.Counts.ServicesStoppedOrUnknown);
        CollectionAssert.AreEquivalent(new[] { "offline", "revisionDrift", "failedService" },
            result.Issues.Select(issue => issue.Kind).ToArray());
        Assert.AreEqual("safe message", result.Issues.Single(issue => issue.Kind == "failedService").ErrorMessage);
    }

    [TestMethod]
    public async Task MigrateAsync_ServiceTablesHaveExpectedColumnsAndForeignKeys()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await using var connection = await fixture.OpenConnectionAsync();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "id", "node_id", "name", "backend_type", "backend_version", "enabled", "config_schema_version",
                "config_json", "created_at_utc", "updated_at_utc", "backend_update_policy"
            },
            await ReadColumnNamesAsync(connection, "service_instances"));
        CollectionAssert.AreEquivalent(
            new[]
            {
                "service_id", "status", "backend_version", "applied_config_sha256", "traffic_upload_bytes",
                "traffic_download_bytes", "traffic_observed_at_utc", "observed_at_utc", "error_code", "error_message"
            },
            await ReadColumnNamesAsync(connection, "service_runtime_states"));

        var foreignKeys = await ReadForeignKeysAsync(connection, "service_runtime_states");
        CollectionAssert.Contains(foreignKeys, ("service_instances", "service_id", "id", "RESTRICT"));
        foreignKeys = await ReadForeignKeysAsync(connection, "service_instances");
        CollectionAssert.Contains(foreignKeys, ("nodes", "node_id", "id", "RESTRICT"));
    }

    [TestMethod]
    public async Task MigrateAsync_CreatesPhase4Tables()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        await using var connection = await fixture.OpenConnectionAsync();

        var tables = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name IN ('users', 'user_sessions', 'user_service_bindings', 'usage_batches', 'usage_totals') ORDER BY name;";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        CollectionAssert.AreEquivalent(
            new[] { "usage_batches", "usage_totals", "user_service_bindings", "user_sessions", "users" }, tables);
    }

    [TestMethod]
    public async Task ServiceMutations_CreateUpdateDeleteAdvanceRevisionAndPreserveDesiredFields()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "token", "secret");
        var service = XrayService(CreateService(nodeId, Guid.NewGuid(), "initial"));

        var created = await fixture.Repository.CreateServiceAsync(service, CancellationToken.None);
        Assert.AreEqual(1L, created.Revision);
        var desired = await fixture.Repository.GetDesiredStateForAgentAsync(agentId, CancellationToken.None);
        Assert.IsNotNull(desired);
        Assert.AreEqual(1L, desired.Value.Revision);
        Assert.AreEqual(service, desired.Value.Services.Single());

        var updated = service with
        {
            Name = "updated", BackendVersion = "2.0", Enabled = false, ConfigSchemaVersion = 4,
            ConfigJson = "{\"port\":443}", UpdatedAtUtc = fixture.Time.GetUtcNow().AddMinutes(1)
        };
        var updateResult = await fixture.Repository.UpdateServiceAsync(updated, CancellationToken.None);
        Assert.AreEqual(2L, updateResult.Revision);
        Assert.AreEqual(updated,
            (await fixture.Repository.GetDesiredStateForAgentAsync(await GetAgentIdAsync(fixture, nodeId),
                CancellationToken.None))!.Value.Services.Single());

        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Delete", "delete", "hash", "User",
            true, null, null, "delete-token", CancellationToken.None);
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, service.Id, CancellationToken.None));
        Assert.IsTrue(await fixture.Repository.SetServicePublicEndpointAsync(nodeId,
            new ServicePublicEndpointRecord(service.Id, "delete.example", 443, null, fixture.Time.GetUtcNow()),
            CancellationToken.None));

        var deleteRevision = await fixture.Repository.DeleteServiceAsync(nodeId, service.Id, CancellationToken.None);
        Assert.AreEqual(4L, deleteRevision);
        Assert.AreEqual(0, (await fixture.Repository.GetServicesForNodeAsync(nodeId, CancellationToken.None)).Count);
        Assert.AreEqual(0,
            (await fixture.Repository.GetBoundServicesAsync(user.User.Id, CancellationToken.None)).Count);
        Assert.IsNull(await fixture.Repository.GetServicePublicEndpointAsync(nodeId, service.Id,
            CancellationToken.None));
    }

    [TestMethod]
    public async Task DeleteNode_RemovesEveryReferenceAndKeepsRevocationTombstone()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var keptNodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "delete-node-token", "delete-node-secret");
        await CreateAgentAsync(fixture, keptNodeId, "kept-node-token", "kept-node-secret");
        var service = await fixture.Repository.CreateServiceAsync(XrayService(CreateService(nodeId, Guid.NewGuid(), "doomed")),
            CancellationToken.None);
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Bound", "bound", "hash", "User",
            true, null, null, "bound-token", CancellationToken.None);
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, service.Service!.Id, CancellationToken.None));
        Assert.IsNotNull(await fixture.Repository.CreateCollectServiceLogsCommandAsync(Guid.NewGuid(), nodeId,
            service.Service.Id, fixture.Time.GetUtcNow().AddMinutes(2), CancellationToken.None));

        Assert.IsTrue(await fixture.Repository.DeleteNodeAsync(nodeId, CancellationToken.None));

        Assert.IsFalse(await fixture.Repository.DeleteNodeAsync(nodeId, CancellationToken.None));
        Assert.IsNull(await fixture.Repository.GetAgentAuthenticationAsync(agentId, CancellationToken.None));
        Assert.IsNotNull(await fixture.Repository.GetRevokedAgentSecretHashAsync(agentId, CancellationToken.None));
        Assert.AreEqual(0, (await fixture.Repository.GetBoundServicesAsync(user.User.Id, CancellationToken.None)).Count);
        var remaining = await fixture.Repository.GetNodeObservationsAsync(CancellationToken.None);
        Assert.AreEqual(keptNodeId, remaining.Single().Id);
    }

    [TestMethod]
    public async Task Groups_ControlBindingsForMembersAndNewServices()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(nodeId, "grant", CancellationToken.None);
        var first = await fixture.Repository.CreateServiceAsync(CreateService(nodeId, Guid.NewGuid(), "hy-a"), CancellationToken.None);
        var second = await fixture.Repository.CreateServiceAsync(CreateService(nodeId, Guid.NewGuid(), "hy-b"), CancellationToken.None);
        await fixture.Repository.AddServiceToAutoGroupsAsync(first.Service!.Id, CancellationToken.None);
        await fixture.Repository.AddServiceToAutoGroupsAsync(second.Service!.Id, CancellationToken.None);
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Member", "member", "hash", "User",
            true, null, null, "member-token", CancellationToken.None);

        Assert.IsTrue(await fixture.Repository.SetUserGroupAsync(user.User.Id, SqliteServerRepository.DefaultGroupId, CancellationToken.None));
        Assert.AreEqual(2, (await fixture.Repository.GetBoundServicesAsync(user.User.Id, CancellationToken.None)).Count);

        var vip = Guid.NewGuid();
        Assert.IsTrue(await fixture.Repository.SaveGroupAsync(vip, "VIP", false, [second.Service.Id], true, CancellationToken.None));
        Assert.IsFalse(await fixture.Repository.SaveGroupAsync(Guid.NewGuid(), "vip", false, [], true, CancellationToken.None),
            "group names are unique ignoring case");
        Assert.IsTrue(await fixture.Repository.SetUserGroupAsync(user.User.Id, vip, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { second.Service.Id },
            (await fixture.Repository.GetBoundServicesAsync(user.User.Id, CancellationToken.None)).ToArray());

        var third = await fixture.Repository.CreateServiceAsync(CreateService(nodeId, Guid.NewGuid(), "hy-c"), CancellationToken.None);
        await fixture.Repository.AddServiceToAutoGroupsAsync(third.Service!.Id, CancellationToken.None);
        Assert.AreEqual(1, (await fixture.Repository.GetBoundServicesAsync(user.User.Id, CancellationToken.None)).Count,
            "a group without auto-include does not receive new services");

        Assert.AreEqual(true, await fixture.Repository.DeleteGroupAsync(vip, CancellationToken.None));
        Assert.AreEqual(3, (await fixture.Repository.GetBoundServicesAsync(user.User.Id, CancellationToken.None)).Count,
            "members of a deleted group fall back to the default group");
        Assert.IsNull(await fixture.Repository.DeleteGroupAsync(SqliteServerRepository.DefaultGroupId, CancellationToken.None));
    }

    [TestMethod]
    public async Task SubscriptionToken_IsRecoverableAfterCreateAndRotate()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Viewer", "viewer", "hash", "User",
            true, null, null, "first-token", CancellationToken.None);
        Assert.AreEqual("first-token", await fixture.Repository.GetSubscriptionTokenAsync(user.User.Id, CancellationToken.None));

        await fixture.Repository.RotateSubscriptionTokenAsync(user.User.Id, "second-token", CancellationToken.None);

        Assert.AreEqual("second-token", await fixture.Repository.GetSubscriptionTokenAsync(user.User.Id, CancellationToken.None));
        Assert.IsNull(await fixture.Repository.GetSubscriptionTokenAsync(Guid.NewGuid(), CancellationToken.None));
    }

    [TestMethod]
    public async Task CertificateSourceWatcher_DistributesRenewedFilesAndKeepsOldOnInvalidFiles()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var directory = Directory.CreateTempSubdirectory("hypanel-cert-");
        try
        {
            var certPath = Path.Combine(directory.FullName, "fullchain.pem");
            var keyPath = Path.Combine(directory.FullName, "privkey.pem");
            var now = DateTimeOffset.UtcNow;
            var (firstPem, firstKey) = HyPanel.Server.Tests.Endpoints.AdminCertificatesEndpointsTests.SelfSigned("hy.example.com", now);
            await File.WriteAllTextAsync(certPath, firstPem);
            await File.WriteAllTextAsync(keyPath, firstKey);
            Assert.IsTrue(AdminCertificatesEndpoints.TryCreate(Guid.NewGuid(), new CertificateUploadRequest("lucky",
                firstPem, firstKey, "Path", certPath, keyPath), now, out var created));
            Assert.IsTrue(await fixture.Repository.CreateCertificateAsync(created, CancellationToken.None));
            var nodeId = Guid.NewGuid();
            await fixture.Repository.CreateNodeAsync(nodeId, "cert-node", CancellationToken.None);
            await fixture.Repository.CreateServiceAsync(CreateService(nodeId, Guid.NewGuid(), "hy") with
            {
                ConfigJson = $$"""{"certificateId":"{{created.Id:D}}"}"""
            }, CancellationToken.None);
            var revision = (await fixture.Repository.GetNodeAsync(nodeId, CancellationToken.None))!.DesiredRevision;
            var watcher = new HyPanel.Server.Security.CertificateSourceWatcher(fixture.Repository, TimeProvider.System,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<HyPanel.Server.Security.CertificateSourceWatcher>.Instance);

            var (renewedPem, renewedKey) = HyPanel.Server.Tests.Endpoints.AdminCertificatesEndpointsTests.SelfSigned("hy.example.com", now, 90);
            await File.WriteAllTextAsync(certPath, renewedPem);
            await File.WriteAllTextAsync(keyPath, renewedKey);
            await watcher.CheckAllAsync(CancellationToken.None);

            var renewed = await fixture.Repository.GetCertificateWithKeyAsync(created.Id, CancellationToken.None);
            Assert.AreNotEqual(created.Fingerprint, renewed!.Fingerprint);
            Assert.AreEqual(renewedKey.Trim() + "\n", renewed.PrivateKeyPem);
            Assert.IsNull(renewed.SourceError);
            Assert.AreEqual(revision + 1,
                (await fixture.Repository.GetNodeAsync(nodeId, CancellationToken.None))!.DesiredRevision,
                "nodes using the certificate receive the renewed copy");

            await File.WriteAllTextAsync(certPath, "garbage");
            await watcher.CheckAllAsync(CancellationToken.None);
            var kept = await fixture.Repository.GetCertificateWithKeyAsync(created.Id, CancellationToken.None);
            Assert.AreEqual(renewed.Fingerprint, kept!.Fingerprint);
            Assert.IsNotNull(kept.SourceError);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [DataTestMethod]
    [DataRow("DNS:hy.example.com, DNS:www.example.com", "hy.example.com")]
    [DataRow("DNS:*.example.com, DNS=edge.example.com", "edge.example.com")]
    [DataRow("IP Address:203.0.113.1", null)]
    [DataRow("", null)]
    public void FirstDnsName_PicksFirstConcreteHostName(string san, string? expected) =>
        Assert.AreEqual(expected, SqliteServerRepository.FirstDnsName(san));

    [TestMethod]
    public async Task ServiceMutations_WhenNodeOrServiceDoesNotExist_DoNotAdvanceRevision()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var service = CreateService(nodeId, Guid.NewGuid(), "missing");
        Assert.AreEqual(0L, (await fixture.Repository.CreateServiceAsync(service, CancellationToken.None)).Revision);
        Assert.AreEqual(0L, (await fixture.Repository.UpdateServiceAsync(service, CancellationToken.None)).Revision);
        Assert.IsNull(await fixture.Repository.DeleteServiceAsync(nodeId, service.Id, CancellationToken.None));
        Assert.IsNull(await fixture.Repository.GetNodeAsync(nodeId, CancellationToken.None));
    }

    [TestMethod]
    public async Task BackendUpdatePolicyAndRequest_KeepDesiredRunningAndPolicySeparate()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "backend-update-token", "backend-update-secret");
        var service = XrayService(CreateService(nodeId, Guid.NewGuid(), "update-target"));
        var created = await fixture.Repository.CreateServiceAsync(service, CancellationToken.None);
        Assert.AreEqual(1L, created.Revision);
        Assert.IsTrue(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "1.0.0", "linux-x64", 1, null,
            [new ServiceRuntimeState(service.Id, ServiceRuntimeStatus.Running, service.BackendVersion, null, null,
                fixture.Time.GetUtcNow(), null, null)], [], CancellationToken.None));

        var target = await fixture.Repository.GetBackendUpdateTargetAsync(nodeId, service.Id, CancellationToken.None);
        Assert.IsNotNull(target);
        Assert.AreEqual("Manual", target.Policy);
        Assert.AreEqual("linux-x64", target.ReportedRid);
        Assert.IsTrue(await fixture.Repository.SetBackendUpdatePolicyAsync(nodeId, service.Id, "Auto",
            CancellationToken.None));
        var revision = await fixture.Repository.RequestBackendUpdateAsync(nodeId, service.Id, "26.9.8",
            CancellationToken.None);

        Assert.AreEqual(2L, revision);
        var row = (await fixture.Repository.GetServicesForNodeAsync(nodeId, CancellationToken.None)).Single();
        Assert.AreEqual("26.9.8", row.Service.BackendVersion);
        Assert.AreEqual("Auto", row.Service.BackendUpdatePolicy);
        Assert.AreEqual("26.3.27", row.Runtime!.BackendVersion);
    }

    [TestMethod]
    public async Task AgentSync_BackendRollbackReport_RestoresDesiredVersionAndAdvancesRevisionOnce()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "rollback-token", "rollback-secret");
        var service = XrayService(CreateService(nodeId, Guid.NewGuid(), "rollback"));
        await fixture.Repository.CreateServiceAsync(service, CancellationToken.None);
        Assert.AreEqual(2L, await fixture.Repository.RequestBackendUpdateAsync(nodeId, service.Id, "26.9.8",
            CancellationToken.None));
        var report = new ServiceRuntimeState(service.Id, ServiceRuntimeStatus.Running, "26.3.27", null, null,
            fixture.Time.GetUtcNow(), "backend_update_rolled_back",
            "Backend update failed and the previous version was restored.");

        Assert.IsTrue(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "1.0.0", "linux-x64", 1, null,
            [report], [], CancellationToken.None));
        var desired = await fixture.Repository.GetDesiredStateForAgentAsync(agentId, CancellationToken.None);
        Assert.AreEqual(3L, desired!.Value.Revision);
        Assert.AreEqual("26.3.27", desired.Value.Services.Single().BackendVersion);
        Assert.IsTrue(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "1.0.0", "linux-x64", 1, null,
            [report], [], CancellationToken.None));
        Assert.AreEqual(3L, (await fixture.Repository.GetNodeAsync(nodeId, CancellationToken.None))!.DesiredRevision);
    }

    [TestMethod]
    public async Task AgentSync_UpsertsRuntimeOnlyForAgentOwnedNodeAndPreservesTerminalAndTrafficFields()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var otherNodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "token", "secret");
        await CreateAgentAsync(fixture, otherNodeId, "other-token", "other-secret");
        var service = CreateService(nodeId, Guid.NewGuid(), "owned");
        var otherService = CreateService(otherNodeId, Guid.NewGuid(), "other");
        await fixture.Repository.CreateServiceAsync(service, CancellationToken.None);
        await fixture.Repository.CreateServiceAsync(otherService, CancellationToken.None);
        var observedAt = fixture.Time.GetUtcNow().AddSeconds(5);
        var trafficAt = observedAt.AddSeconds(-1);
        var state = new ServiceRuntimeState(service.Id, ServiceRuntimeStatus.Failed, "2.1", "abc",
            new BackendTrafficSnapshot(11, 22, trafficAt), observedAt, "E_FAIL", "terminal");
        var foreignState = state with { ServiceId = otherService.Id };

        Assert.IsTrue(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "1", "linux-x64", 0, null,
            [state, foreignState], [], CancellationToken.None));
        var records = await fixture.Repository.GetServicesForNodeAsync(nodeId, CancellationToken.None);
        var runtime = records.Single().Runtime;
        Assert.IsNotNull(runtime);
        Assert.AreEqual((int)ServiceRuntimeStatus.Failed, runtime.Status);
        Assert.AreEqual(11L, runtime.TrafficUploadBytes);
        Assert.AreEqual(22L, runtime.TrafficDownloadBytes);
        Assert.AreEqual(trafficAt, runtime.TrafficObservedAtUtc);
        Assert.AreEqual("E_FAIL", runtime.ErrorCode);
        Assert.IsNull((await fixture.Repository.GetServicesForNodeAsync(otherNodeId, CancellationToken.None)).Single()
            .Runtime);
    }

    [TestMethod]
    public async Task TryConsumeEnrollmentTokenAndCreateAgentAsync_ConsumesOnlyOnceAndNeverPersistsPlaintextSecrets()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        const string token = "token-plaintext-must-not-be-stored";
        const string secret = "agent-secret-plaintext-must-not-be-stored";

        await fixture.Repository.CreateNodeAsync(nodeId, "node", CancellationToken.None);
        await fixture.Repository.CreateEnrollmentTokenAsync(Guid.NewGuid(), nodeId, token,
            fixture.Time.GetUtcNow().AddMinutes(5), CancellationToken.None);

        var firstEnrollment = await fixture.Repository.TryConsumeEnrollmentTokenAndCreateAgentAsync(
            token, Guid.NewGuid(), secret, "1.0.0", "linux-x64", CancellationToken.None);
        var secondEnrollment = await fixture.Repository.TryConsumeEnrollmentTokenAndCreateAgentAsync(
            token, Guid.NewGuid(), "another-secret", "1.0.0", "linux-x64", CancellationToken.None);

        Assert.IsNotNull(firstEnrollment);
        Assert.IsNull(secondEnrollment);

        await using var connection = await fixture.OpenConnectionAsync();
        await using var hashCommand = connection.CreateCommand();
        hashCommand.CommandText =
            "SELECT (SELECT length(token_hash) FROM enrollment_tokens), (SELECT length(secret_hash) FROM agents);";
        await using var hashes = await hashCommand.ExecuteReaderAsync();
        Assert.IsTrue(await hashes.ReadAsync());
        Assert.AreEqual(32L, hashes.GetInt64(0));
        Assert.AreEqual(32L, hashes.GetInt64(1));

        var textValues = await ReadAllTextValuesAsync(connection);
        Assert.IsFalse(textValues.Any(value => value.Contains(token, StringComparison.Ordinal)));
        Assert.IsFalse(textValues.Any(value => value.Contains(secret, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task TryConsumeEnrollmentTokenAndCreateAgentAsync_ReturnsNullForExpiredToken()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        const string token = "expired-token";

        await fixture.Repository.CreateNodeAsync(nodeId, "node", CancellationToken.None);
        await fixture.Repository.CreateEnrollmentTokenAsync(Guid.NewGuid(), nodeId, token,
            fixture.Time.GetUtcNow().AddMinutes(1), CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        var enrollment = await fixture.Repository.TryConsumeEnrollmentTokenAndCreateAgentAsync(
            token, Guid.NewGuid(), "secret", "1.0.0", "linux-x64", CancellationToken.None);

        Assert.IsNull(enrollment);
    }

    [TestMethod]
    public async Task TryConsumeEnrollmentTokenAndCreateAgentAsync_ReEnrollmentRotatesExistingNodeIdentity()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var originalAgentId = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(nodeId, "node", CancellationToken.None);
        await fixture.Repository.CreateEnrollmentTokenAsync(Guid.NewGuid(), nodeId, "first-token",
            fixture.Time.GetUtcNow().AddMinutes(5), CancellationToken.None);
        var first = await fixture.Repository.TryConsumeEnrollmentTokenAndCreateAgentAsync(
            "first-token", originalAgentId, "first-secret", "1.0.0", "linux-x64", CancellationToken.None);
        Assert.IsNotNull(first);
        Assert.IsTrue(await fixture.Repository.TryUpdateAgentReportAsync(
            originalAgentId, "1.0.0", "linux-x64", 4, "metrics", CancellationToken.None));

        await fixture.Repository.CreateEnrollmentTokenAsync(Guid.NewGuid(), nodeId, "second-token",
            fixture.Time.GetUtcNow().AddMinutes(5), CancellationToken.None);
        var second = await fixture.Repository.TryConsumeEnrollmentTokenAndCreateAgentAsync(
            "second-token", Guid.NewGuid(), "second-secret", "2.0.0", "linux-arm64", CancellationToken.None);

        Assert.IsNotNull(second);
        Assert.AreEqual(originalAgentId, second.AgentId);
        var agent = await fixture.Repository.GetAgentAsync(originalAgentId, CancellationToken.None);
        Assert.IsNotNull(agent);
        Assert.IsNull(agent.LastSeenAtUtc);
        Assert.AreEqual("2.0.0", agent.ReportedVersion);
        Assert.AreEqual("linux-arm64", agent.ReportedPlatform);
        Assert.AreEqual(0L, agent.AppliedRevision);
        Assert.IsNull(agent.LatestMetricSnapshotJson);
        var authentication = await fixture.Repository.GetAgentAuthenticationAsync(originalAgentId, CancellationToken.None);
        Assert.IsNotNull(authentication);
        CollectionAssert.AreEqual(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("second-secret")),
            authentication.SecretHash);
    }

    [TestMethod]
    public void PasswordService_VerifiesValidPasswordAndRejectsWrongOrMalformedValues()
    {
        var service = new HyPanel.Server.PasswordService();
        var encoded = service.Hash("correct-password");

        Assert.IsTrue(service.Verify("correct-password", encoded));
        Assert.IsFalse(service.Verify("wrong-password", encoded));
        Assert.IsFalse(service.Verify("correct-password", "not-a-pbkdf2-record"));
        Assert.IsFalse(service.Verify("correct-password", "pbkdf2-sha256$210000$%%%$%%%"));
        Assert.IsFalse(service.Verify("correct-password",
            "pbkdf2-sha256$1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA="));
    }

    [TestMethod]
    public async Task UserCredentials_SessionsAndSubscriptionTokensAreHashedAndLifecycleIsEnforced()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var password = new HyPanel.Server.PasswordService();
        var userId = Guid.NewGuid();
        const string passwordText = "user-password-plaintext";
        const string sessionToken = "session-token-plaintext";
        const string subscriptionToken = "subscription-token-plaintext";
        var user = await fixture.Repository.CreateUserAsync(userId, "Alice", "alice", password.Hash(passwordText),
            "User", true, null, null, subscriptionToken, CancellationToken.None);
        var session = await fixture.Repository.CreateSessionAsync(user.User, sessionToken,
            fixture.Time.GetUtcNow().AddMinutes(5), CancellationToken.None);

        Assert.IsNotNull(await fixture.Repository.AuthenticateSessionAsync(sessionToken, CancellationToken.None));
        await fixture.Repository.RevokeSessionAsync(sessionToken, CancellationToken.None);
        Assert.IsNull(await fixture.Repository.AuthenticateSessionAsync(sessionToken, CancellationToken.None));

        var secondToken = "second-session-token";
        await fixture.Repository.CreateSessionAsync(user.User, secondToken, fixture.Time.GetUtcNow().AddMinutes(5),
            CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.IsNull(await fixture.Repository.AuthenticateSessionAsync(secondToken, CancellationToken.None));

        var enabledUser = await fixture.Repository.UpdateUserAsync(userId, "Alice", "alice", null, "User", false, null,
            null, CancellationToken.None);
        Assert.IsNotNull(enabledUser);
        var thirdToken = "disabled-user-session";
        await fixture.Repository.CreateSessionAsync(enabledUser!, thirdToken, fixture.Time.GetUtcNow().AddMinutes(5),
            CancellationToken.None);
        Assert.IsNull(await fixture.Repository.AuthenticateSessionAsync(thirdToken, CancellationToken.None));

        await fixture.Repository.UpdateUserAsync(userId, "Alice", "alice", null, "User", true, null,
            fixture.Time.GetUtcNow().AddMinutes(1), CancellationToken.None);
        var fourthToken = "expired-user-session";
        await fixture.Repository.CreateSessionAsync(
            (await fixture.Repository.GetUserAsync(userId, CancellationToken.None))!, fourthToken,
            fixture.Time.GetUtcNow().AddMinutes(5), CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.IsNull(await fixture.Repository.AuthenticateSessionAsync(fourthToken, CancellationToken.None));

        var oldHash = await ReadBlobAsync(fixture, "SELECT subscription_token_hash FROM users WHERE id = @id", userId);
        const string rotatedToken = "rotated-subscription-token";
        Assert.AreEqual(rotatedToken,
            await fixture.Repository.RotateSubscriptionTokenAsync(userId, rotatedToken, CancellationToken.None));
        var newHash = await ReadBlobAsync(fixture, "SELECT subscription_token_hash FROM users WHERE id = @id", userId);
        CollectionAssert.AreNotEqual(oldHash, newHash);
        CollectionAssert.AreEqual(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(rotatedToken)), newHash);

        var textValues = await ReadAllTextValuesAsync(await fixture.OpenConnectionAsync());
        Assert.IsFalse(textValues.Any(value => value.Contains(passwordText, StringComparison.Ordinal)));
        Assert.IsFalse(textValues.Any(value => value.Contains(sessionToken, StringComparison.Ordinal)));
        Assert.IsFalse(textValues.Any(value => value.Contains(subscriptionToken, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task ServiceBindings_AreIdempotentAndRejectMissingUserOrService()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Bob", "bob", "password-hash", "User", true,
            null, null, "subscription", CancellationToken.None);
        var service = XrayService(CreateService(nodeId, Guid.NewGuid(), "bound"));
        await fixture.Repository.CreateNodeAsync(nodeId, "node", CancellationToken.None);
        await fixture.Repository.CreateServiceAsync(service, CancellationToken.None);

        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, service.Id, CancellationToken.None));
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, service.Id, CancellationToken.None));
        CollectionAssert.AreEqual(new[] { service.Id },
            (await fixture.Repository.GetBoundServicesAsync(user.User.Id, CancellationToken.None)).ToArray());
        Assert.IsFalse(await fixture.Repository.BindServiceAsync(Guid.NewGuid(), service.Id, CancellationToken.None));
        Assert.IsFalse(await fixture.Repository.BindServiceAsync(user.User.Id, Guid.NewGuid(), CancellationToken.None));
    }

    [TestMethod]
    public async Task XrayBindings_CreateDistinctEncryptedCredentialsAndRotateOrRevokeIndependently()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(nodeId, "node", CancellationToken.None);
        var service = XrayService(CreateService(nodeId, Guid.NewGuid(), "xray"));
        await fixture.Repository.CreateServiceAsync(service, CancellationToken.None);
        var first = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "First", "first", "hash", "User", true,
            null, null, "first-token", CancellationToken.None);
        var second = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Second", "second", "hash", "User", true,
            null, null, "second-token", CancellationToken.None);
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(first.User.Id, service.Id, CancellationToken.None));
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(second.User.Id, service.Id, CancellationToken.None));
        var credentials = await fixture.Repository.GetServiceCredentialsAsync(service.Id, true, CancellationToken.None);
        Assert.AreEqual(2, credentials.Count);
        Assert.AreNotEqual(credentials[0].Credential, credentials[1].Credential);
        var oldFirst = credentials.Single(item => item.UserId == first.User.Id).Credential;

        var rotated = await fixture.Repository.RotateServiceCredentialAsync(first.User.Id, service.Id,
            CancellationToken.None);
        Assert.IsNotNull(rotated);
        Assert.AreNotEqual(oldFirst, rotated.Credential);
        Assert.AreEqual(credentials.Single(item => item.UserId == second.User.Id).Credential,
            (await fixture.Repository.GetServiceCredentialsAsync(service.Id, true, CancellationToken.None))
            .Single(item => item.UserId == second.User.Id).Credential);
        Assert.IsNotNull(await fixture.Repository.RevokeServiceCredentialAsync(first.User.Id, service.Id,
            CancellationToken.None));
        Assert.AreEqual(1, (await fixture.Repository.GetServiceCredentialsAsync(service.Id, true,
            CancellationToken.None)).Count);

        await using var connection = await fixture.OpenConnectionAsync();
        var text = await ReadAllTextValuesAsync(connection);
        Assert.IsFalse(text.Any(value => value.Contains(oldFirst, StringComparison.Ordinal) ||
                                        value.Contains(rotated.Credential, StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task PublicSubscription_RequiresEligibleRotatedTokenAndProjectsOnlyClientFieldsInAllFormats()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var service = XrayService(CreateService(nodeId, Guid.NewGuid(), "My service")) with
        {
            ConfigJson =
            "{\"flow\":\"xtls-rprx-vision\",\"realityPublicKey\":\"public-key\",\"shortId\":\"a1b2\",\"serverName\":\"sni.example\",\"fingerprint\":\"chrome\",\"realityPrivateKey\":\"private-key\",\"destination\":\"private.example:443\"}"
        };
        await fixture.Repository.CreateNodeAsync(nodeId, "node", CancellationToken.None);
        await fixture.Repository.CreateServiceAsync(service, CancellationToken.None);
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Sub", "sub", "password-hash", "User", true,
            100, null, "old-token", CancellationToken.None);
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, service.Id, CancellationToken.None));
        var credential = (await fixture.Repository.GetServiceCredentialsAsync(service.Id, true,
            CancellationToken.None)).Single().Credential;
        var endpoint =
            new ServicePublicEndpointRecord(service.Id, "sub.example", 443, "sni.example", fixture.Time.GetUtcNow());
        Assert.IsTrue(await fixture.Repository.SetServicePublicEndpointAsync(nodeId, endpoint, CancellationToken.None));
        Assert.IsFalse(
            await fixture.Repository.SetServicePublicEndpointAsync(Guid.NewGuid(), endpoint, CancellationToken.None));
        Assert.AreEqual(endpoint,
            await fixture.Repository.GetServicePublicEndpointAsync(nodeId, service.Id, CancellationToken.None));
        await AssertSubscriptionStatusAsync(fixture, "unknown-token", StatusCodes.Status404NotFound);

        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            await SubscriptionEndpoints.GetAsync("old-token", context.Response, fixture.Repository,
                CancellationToken.None);
            Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.AreEqual("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.AreEqual("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
            StringAssert.StartsWith(context.Response.ContentType, "application/yaml");
            StringAssert.StartsWith(context.Response.Headers["subscription-userinfo"].ToString(), "upload=0; download=0; total=");
            context.Response.Body.Position = 0;
            var content = await new StreamReader(context.Response.Body).ReadToEndAsync();
            StringAssert.Contains(content, credential);
            Assert.IsFalse(content.Contains("private-key", StringComparison.Ordinal));
            Assert.IsFalse(content.Contains("private.example", StringComparison.Ordinal));
            Assert.IsFalse(content.Contains("upMbps", StringComparison.Ordinal));
        }

        Assert.AreEqual("new-token",
            await fixture.Repository.RotateSubscriptionTokenAsync(user.User.Id, "new-token", CancellationToken.None));
        await AssertSubscriptionStatusAsync(fixture, "old-token", StatusCodes.Status404NotFound);
        await AssertSubscriptionStatusAsync(fixture, "new-token", StatusCodes.Status200OK);

        await fixture.Repository.UpdateUserAsync(user.User.Id, "Sub", "sub", null, "User", true, 100,
            fixture.Time.GetUtcNow(), CancellationToken.None);
        await AssertSubscriptionStatusAsync(fixture, "new-token", StatusCodes.Status404NotFound);
        await fixture.Repository.UpdateUserAsync(user.User.Id, "Sub", "sub", null, "User", true, 100, null,
            CancellationToken.None);
        await using (var connection = await fixture.OpenConnectionAsync())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "INSERT INTO usage_totals (user_id,service_id,upload_bytes,download_bytes,updated_at_utc) VALUES (@user,@service,40,60,@now);";
            command.Parameters.AddWithValue("@user", user.User.Id.ToString("D"));
            command.Parameters.AddWithValue("@service", service.Id.ToString("D"));
            command.Parameters.AddWithValue("@now", fixture.Time.GetUtcNow().ToString("O"));
            await command.ExecuteNonQueryAsync();
        }

        await AssertSubscriptionStatusAsync(fixture, "new-token", StatusCodes.Status404NotFound);

        await fixture.Repository.UpdateUserAsync(user.User.Id, "Sub", "sub", null, "User", false, 100, null,
            CancellationToken.None);
        await AssertSubscriptionStatusAsync(fixture, "new-token", StatusCodes.Status404NotFound);

        var empty = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Empty", "empty", "password-hash",
            "User", true, null, null, "empty-token", CancellationToken.None);
        var emptyContext = new DefaultHttpContext();
        emptyContext.Response.Body = new MemoryStream();
        await SubscriptionEndpoints.GetAsync("empty-token", emptyContext.Response, fixture.Repository,
            CancellationToken.None);
        Assert.AreEqual(StatusCodes.Status200OK, emptyContext.Response.StatusCode);
        emptyContext.Response.Body.Position = 0;
        StringAssert.Contains(await new StreamReader(emptyContext.Response.Body).ReadToEndAsync(), "proxies: []");
        Assert.IsNotNull(empty.User);
    }

    [TestMethod]
    public async Task PublicSubscription_UsesReportedIpv4WhenManualEndpointIsMissing()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "auto-endpoint-token", "auto-endpoint-secret");
        var service = XrayService(CreateService(nodeId, Guid.NewGuid(), "automatic endpoint")) with
        {
            ConfigJson =
                "{\"listenPort\":8443,\"flow\":\"xtls-rprx-vision\",\"realityPublicKey\":\"public-key\",\"shortId\":\"a1b2\",\"serverName\":\"sni.example\",\"fingerprint\":\"chrome\"}"
        };
        await fixture.Repository.CreateServiceAsync(service, CancellationToken.None);
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Auto", "auto", "password-hash",
            "User", true, null, null, "auto-token", CancellationToken.None);
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, service.Id, CancellationToken.None));
        Assert.IsTrue(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "1.0.0", "linux-x64", 0, null,
            [], [], CancellationToken.None, publicIpv4: "203.0.113.42"));

        var mihomo = await RenderSubscriptionAsync(fixture, "auto-token");

        StringAssert.Contains(mihomo, "server: \"203.0.113.42\"");
        StringAssert.Contains(mihomo, "port: 8443");
        Assert.AreEqual("203.0.113.42",
            (await fixture.Repository.GetNodeObservationsAsync(CancellationToken.None)).Single().PublicIpv4);

        Assert.IsTrue(await fixture.Repository.SetServicePublicEndpointAsync(nodeId,
            new ServicePublicEndpointRecord(service.Id, "manual.example", 443, null, fixture.Time.GetUtcNow()),
            CancellationToken.None));
        mihomo = await RenderSubscriptionAsync(fixture, "auto-token");
        StringAssert.Contains(mihomo, "server: \"manual.example\"");
        StringAssert.Contains(mihomo, "port: 443");
    }

    [TestMethod]
    public async Task PublicSubscription_MixedXrayAndHysteria_ProjectsExactClientFieldsWithoutSecrets()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(nodeId, "mixed", CancellationToken.None);
        var hysteria = CreateService(nodeId, Guid.NewGuid(), "hy two") with
        {
            ConfigSchemaVersion = 1,
            ConfigJson = "{\"authPassword\":\"hy-secret\"}"
        };
        const string privateKey = "private-secret-must-not-leak";
        const string publicKey = "public-key-value";
        var xray = CreateService(nodeId, Guid.NewGuid(), "xray # one") with
        {
            BackendType = "xray",
            BackendVersion = "26.3.27",
            ConfigSchemaVersion = 1,
            ConfigJson =
            $$"""{"listenHost":"0.0.0.0","listenPort":24445,"flow":"xtls-rprx-vision","realityPrivateKey":"{{privateKey}}","realityPublicKey":"{{publicKey}}","shortId":"a1b2","serverName":"www.example.com","destination":"private-destination.example:443","fingerprint":"chrome"}"""
        };
        await fixture.Repository.CreateServiceAsync(hysteria, CancellationToken.None);
        await fixture.Repository.CreateServiceAsync(xray, CancellationToken.None);
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Mixed", "mixed", "hash", "User", true,
            null, null, "mixed-token", CancellationToken.None);
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, hysteria.Id, CancellationToken.None));
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, xray.Id, CancellationToken.None));
        var hyCredential = (await fixture.Repository.GetServiceCredentialsAsync(hysteria.Id, true,
            CancellationToken.None)).Single().Credential;
        var hyUser = $"hypanel-{user.User.Id:N}";
        var clientId = (await fixture.Repository.GetServiceCredentialsAsync(xray.Id, true,
            CancellationToken.None)).Single().Credential;
        Assert.IsTrue(await fixture.Repository.SetServicePublicEndpointAsync(nodeId,
            new ServicePublicEndpointRecord(hysteria.Id, "hy.example", 24444, "hy.example", fixture.Time.GetUtcNow()),
            CancellationToken.None));
        Assert.IsTrue(await fixture.Repository.SetServicePublicEndpointAsync(nodeId,
            new ServicePublicEndpointRecord(xray.Id, "2001:db8::10", 24445, null, fixture.Time.GetUtcNow()),
            CancellationToken.None));

        var mihomo = await RenderSubscriptionAsync(fixture, "mixed-token");
        StringAssert.Contains(mihomo, "type: hysteria2");
        // Default template: proxies expand into the placeholder groups and the routing rules are present.
        StringAssert.Contains(mihomo, "  - name: 🚀 手动切换\n    type: select\n    proxies:\n      - \"mixed · hy two\"\n      - \"mixed · xray # one\"");
        StringAssert.Contains(mihomo, "  - MATCH,🐟 漏网之鱼");
        Assert.IsFalse(mihomo.Contains("- __ALL_PROXIES__", StringComparison.Ordinal));
        Assert.IsFalse(mihomo.Contains("proxies: ~", StringComparison.Ordinal));
        StringAssert.Contains(mihomo, $"password: \"{hyUser}:{hyCredential}\"");
        StringAssert.Contains(mihomo, "type: vless");
        StringAssert.Contains(mihomo, "public-key: \"public-key-value\"");
        StringAssert.Contains(mihomo, "short-id: \"a1b2\"");
        StringAssert.Contains(mihomo, $"uuid: \"{clientId}\"");
        StringAssert.Contains(mihomo, "server: \"2001:db8::10\"\n    port: 24445");
        StringAssert.Contains(mihomo, "sni: \"hy.example\"");

        foreach (var output in new[] { mihomo })
        {
            Assert.IsFalse(output.Contains(privateKey, StringComparison.Ordinal));
            Assert.IsFalse(output.Contains("hy-secret", StringComparison.Ordinal));
            Assert.IsFalse(output.Contains("private-destination.example", StringComparison.Ordinal));
        }

        var redacted = AdminServicesEndpoints.RedactPasswords(xray.ConfigJson);
        StringAssert.Contains(redacted, "[REDACTED]");
        Assert.IsFalse(redacted.Contains(privateKey, StringComparison.Ordinal));
        StringAssert.Contains(redacted, publicKey);
    }

    [TestMethod]
    public async Task PublicSubscription_MihomoAndSingBox_ProjectsSip002AndRedactsPasswords()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(nodeId, "shadowsocks", CancellationToken.None);
        const string mihomoPassword = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";
        const string singBoxPassword = "AQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQE=";
        var mihomo = CreateService(nodeId, Guid.NewGuid(), "mihomo ss") with
        {
            BackendType = "mihomo",
            ConfigJson = $$"""{"method":"chacha20-ietf-poly1305","password":"{{mihomoPassword}}","udp":true}"""
        };
        var singBox = CreateService(nodeId, Guid.NewGuid(), "sing-box ss") with
        {
            BackendType = "sing-box",
            ConfigJson = $$"""{"method":"2022-blake3-aes-256-gcm","password":"{{singBoxPassword}}","udp":true}"""
        };
        var malformed = CreateService(nodeId, Guid.NewGuid(), "malformed ss") with
        {
            BackendType = "mihomo",
            ConfigJson = "{malformed"
        };
        await fixture.Repository.CreateServiceAsync(mihomo, CancellationToken.None);
        await fixture.Repository.CreateServiceAsync(singBox, CancellationToken.None);
        await fixture.Repository.CreateServiceAsync(malformed, CancellationToken.None);
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "SS", "ss", "hash", "User", true,
            null, null, "ss-token", CancellationToken.None);
        foreach (var service in new[] { mihomo, singBox, malformed })
        {
            Assert.IsFalse(await fixture.Repository.BindServiceAsync(user.User.Id, service.Id, CancellationToken.None));
            Assert.IsTrue(await fixture.Repository.SetServicePublicEndpointAsync(nodeId,
                new ServicePublicEndpointRecord(service.Id, service == mihomo ? "mihomo.example" : "singbox.example",
                    service == mihomo ? 443 : 8443, null, fixture.Time.GetUtcNow()), CancellationToken.None));
        }

        var mihomoOutput = await RenderSubscriptionAsync(fixture, "ss-token");
        Assert.IsFalse(mihomoOutput.Contains("type: ss", StringComparison.Ordinal));
        Assert.IsFalse(mihomoOutput.Contains(mihomoPassword, StringComparison.Ordinal));
        Assert.IsFalse(mihomoOutput.Contains(singBoxPassword, StringComparison.Ordinal));

        var redactedMihomo = AdminServicesEndpoints.RedactPasswords(mihomo.ConfigJson);
        var redactedSingBox = AdminServicesEndpoints.RedactPasswords(singBox.ConfigJson);
        Assert.IsFalse(redactedMihomo.Contains(mihomoPassword, StringComparison.Ordinal));
        Assert.IsFalse(redactedSingBox.Contains(singBoxPassword, StringComparison.Ordinal));
        StringAssert.Contains(redactedMihomo, "[REDACTED]");
        StringAssert.Contains(redactedSingBox, "[REDACTED]");
    }

    private static async Task<string> RenderSubscriptionAsync(TestDatabase fixture, string token)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await SubscriptionEndpoints.GetAsync(token, context.Response, fixture.Repository,
            CancellationToken.None);
        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static async Task AssertSubscriptionStatusAsync(TestDatabase fixture, string token, int statusCode)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await SubscriptionEndpoints.GetAsync(token, context.Response, fixture.Repository,
            CancellationToken.None);
        Assert.AreEqual(statusCode, context.Response.StatusCode);
    }

    [TestMethod]
    public async Task AgentSync_UsageIsIdempotentAndInvalidOwnershipOrBindingRollsBack()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "usage-token", "usage-secret");
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Carol", "carol", "password-hash", "User",
            true, null, null, "subscription", CancellationToken.None);
        var service = XrayService(CreateService(nodeId, Guid.NewGuid(), "usage"));
        await fixture.Repository.CreateServiceAsync(service, CancellationToken.None);
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, service.Id, CancellationToken.None));
        var batch = new UsageBatch(Guid.NewGuid(), fixture.Time.GetUtcNow(),
            [new UserUsageDelta(user.User.Id, service.Id, 10, 20)]);

        Assert.IsTrue(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "2.0", "linux-x64", 3, "before", [], [],
            CancellationToken.None, [batch]));
        Assert.IsTrue(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "2.0", "linux-x64", 4, "after", [], [],
            CancellationToken.None, [batch]));
        var totals = await fixture.Repository.GetUsageTotalsAsync(user.User.Id, service.Id, CancellationToken.None);
        Assert.AreEqual(1, totals.Count);
        Assert.AreEqual(10L, totals[0].UploadBytes);
        Assert.AreEqual(20L, totals[0].DownloadBytes);

        var invalidBatch = new UsageBatch(Guid.NewGuid(), fixture.Time.GetUtcNow(),
            [new UserUsageDelta(user.User.Id, Guid.NewGuid(), 1, 1)]);
        Assert.IsFalse(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "2.0", "linux-x64", 5,
            "must-not-commit", [], [], CancellationToken.None, [invalidBatch]));
        Assert.AreEqual(4L,
            (await fixture.Repository.GetNodeObservationsAsync(CancellationToken.None))
            .Single(x => x.AgentId == agentId).AppliedRevision);
        Assert.IsFalse(
            (await fixture.Repository.GetUsageTotalsAsync(null, null, CancellationToken.None)).Any(x =>
                x.UploadBytes == 1));

        var unboundUser = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Dave", "dave", "password-hash",
            "User", true, null, null, "subscription-2", CancellationToken.None);
        var unboundBatch = new UsageBatch(Guid.NewGuid(), fixture.Time.GetUtcNow(),
            [new UserUsageDelta(unboundUser.User.Id, service.Id, 2, 2)]);
        Assert.IsFalse(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "2.0", "linux-x64", 6,
            "must-not-commit", [], [], CancellationToken.None, [unboundBatch]));
        Assert.AreEqual(4L,
            (await fixture.Repository.GetNodeObservationsAsync(CancellationToken.None))
            .Single(x => x.AgentId == agentId).AppliedRevision);
    }

    [TestMethod]
    public async Task AgentSync_UsageOverflowRollsBackWholeSync()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "overflow-token", "overflow-secret");
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Eve", "eve", "password-hash", "User", true,
            null, null, "subscription-3", CancellationToken.None);
        var service = XrayService(CreateService(nodeId, Guid.NewGuid(), "overflow"));
        await fixture.Repository.CreateServiceAsync(service, CancellationToken.None);
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, service.Id, CancellationToken.None));
        var first = new UsageBatch(Guid.NewGuid(), fixture.Time.GetUtcNow(),
            [new UserUsageDelta(user.User.Id, service.Id, long.MaxValue - 1, 0)]);
        Assert.IsTrue(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "2.0", "linux-x64", 1, "committed", [],
            [], CancellationToken.None, [first]));
        var overflow = new UsageBatch(Guid.NewGuid(), fixture.Time.GetUtcNow(),
            [new UserUsageDelta(user.User.Id, service.Id, 2, 0)]);

        Assert.IsFalse(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "2.0", "linux-x64", 2,
            "must-not-commit", [], [], CancellationToken.None, [overflow]));
        var total = (await fixture.Repository.GetUsageTotalsAsync(user.User.Id, service.Id, CancellationToken.None))
            .Single();
        Assert.AreEqual(long.MaxValue - 1, total.UploadBytes);
        Assert.AreEqual(1L,
            (await fixture.Repository.GetNodeObservationsAsync(CancellationToken.None))
            .Single(x => x.AgentId == agentId).AppliedRevision);
    }

    [TestMethod]
    public async Task TryUpdateAgentSyncAsync_RecordsResultsOnlyForTheOwningAgent()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var owningAgentId = await CreateAgentAsync(fixture, "owning-token", "owning-secret");
        var otherAgentId = await CreateAgentAsync(fixture, "other-token", "other-secret");
        var commandId = Guid.NewGuid();
        await fixture.Repository.CreateRunHealthCheckCommandAsync(commandId, owningAgentId,
            fixture.Time.GetUtcNow().AddMinutes(5), CancellationToken.None);

        var updated = await fixture.Repository.TryUpdateAgentSyncAsync(
            otherAgentId, "1.0.0", "linux-x64", 0, null,
            [],
            [
                new AgentCommandResult(commandId, AgentCommandStatus.Succeeded, fixture.Time.GetUtcNow(),
                    fixture.Time.GetUtcNow(), null, null)
            ],
            CancellationToken.None);

        var command = await fixture.Repository.GetAgentCommandAsync(commandId, CancellationToken.None);
        Assert.IsTrue(updated);
        Assert.IsNotNull(command);
        Assert.AreEqual("Pending", command.Status);
        Assert.IsNull(command.CompletedAtUtc);
    }

    [TestMethod]
    public async Task TryUpdateAgentSyncAsync_DoesNotOverwriteSucceededCommandWithFailedResult()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var agentId = await CreateAgentAsync(fixture, "token", "secret");
        var commandId = Guid.NewGuid();
        var completedAt = fixture.Time.GetUtcNow().AddSeconds(10);
        await fixture.Repository.CreateRunHealthCheckCommandAsync(commandId, agentId,
            fixture.Time.GetUtcNow().AddMinutes(5), CancellationToken.None);

        await fixture.Repository.TryUpdateAgentSyncAsync(
            agentId, "1.0.0", "linux-x64", 0, null,
            [],
            [
                new AgentCommandResult(commandId, AgentCommandStatus.Succeeded, fixture.Time.GetUtcNow(), completedAt,
                    null, null)
            ],
            CancellationToken.None);
        await fixture.Repository.TryUpdateAgentSyncAsync(
            agentId, "1.0.0", "linux-x64", 0, null,
            [],
            [
                new AgentCommandResult(commandId, AgentCommandStatus.Failed, fixture.Time.GetUtcNow(),
                    fixture.Time.GetUtcNow().AddSeconds(20), "failed", "must not replace success")
            ],
            CancellationToken.None);

        var command = await fixture.Repository.GetAgentCommandAsync(commandId, CancellationToken.None);
        Assert.IsNotNull(command);
        Assert.AreEqual("Succeeded", command.Status);
        Assert.AreEqual(completedAt, command.CompletedAtUtc);
        Assert.IsNull(command.ErrorCode);
        Assert.IsNull(command.ErrorMessage);
    }

    [TestMethod]
    public async Task GetActiveHealthCheckCommandsAsync_ExcludesExpiredCommands()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var agentId = await CreateAgentAsync(fixture, "token", "secret");
        var expiredCommandId = Guid.NewGuid();
        var activeCommandId = Guid.NewGuid();
        await fixture.Repository.CreateRunHealthCheckCommandAsync(expiredCommandId, agentId,
            fixture.Time.GetUtcNow().AddMinutes(1), CancellationToken.None);
        await fixture.Repository.CreateRunHealthCheckCommandAsync(activeCommandId, agentId,
            fixture.Time.GetUtcNow().AddMinutes(2), CancellationToken.None);
        fixture.Time.Advance(TimeSpan.FromMinutes(1));

        var commands = await fixture.Repository.GetActiveHealthCheckCommandsAsync(agentId, CancellationToken.None);

        Assert.AreEqual(1, commands.Count);
        Assert.AreEqual(activeCommandId, commands[0].Id);
    }

    [TestMethod]
    public async Task Diagnostics_CommandCreation_EnforcesOwnershipAndSingleActiveCommand()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "diagnostic-token", "diagnostic-secret");
        var service = await fixture.Repository.CreateServiceAsync(CreateService(nodeId, Guid.NewGuid(), "diagnostic"),
            CancellationToken.None);

        Assert.IsNull(await fixture.Repository.CreateCollectServiceLogsCommandAsync(Guid.NewGuid(), Guid.NewGuid(),
            service.Service!.Id, fixture.Time.GetUtcNow().AddMinutes(2), CancellationToken.None));

        var commandId = Guid.NewGuid();
        var created = await fixture.Repository.CreateCollectServiceLogsCommandAsync(commandId, nodeId,
            service.Service.Id, fixture.Time.GetUtcNow().AddMinutes(2), CancellationToken.None);
        Assert.IsNotNull(created);
        Assert.AreEqual("CollectServiceLogs", created.Type);
        Assert.AreEqual(service.Service.Id, created.TargetServiceId);
        Assert.IsNull(created.Output);

        Assert.IsNull(await fixture.Repository.CreateCollectServiceLogsCommandAsync(Guid.NewGuid(), nodeId,
            service.Service.Id, fixture.Time.GetUtcNow().AddMinutes(3), CancellationToken.None));

        var healthId = Guid.NewGuid();
        Assert.IsNotNull(await fixture.Repository.CreateRunHealthCheckCommandAsync(healthId, agentId,
            fixture.Time.GetUtcNow().AddMinutes(5), CancellationToken.None));
        var healthChecks = await fixture.Repository.GetActiveHealthCheckCommandsAsync(agentId, CancellationToken.None);
        Assert.AreEqual(1, healthChecks.Count);
        Assert.AreEqual(healthId, healthChecks[0].Id);
        var allActive = await fixture.Repository.GetActiveCommandsAsync(agentId, CancellationToken.None);
        CollectionAssert.AreEquivalent(new[] { commandId, healthId }, allActive.Select(command => command.Id).ToArray());
    }

    [TestMethod]
    public async Task Diagnostics_SyncPersistsBoundedOutput_AndRejectsForgedOutputOnHealthCheck()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "output-token", "output-secret");
        var service = await fixture.Repository.CreateServiceAsync(CreateService(nodeId, Guid.NewGuid(), "logs"),
            CancellationToken.None);

        var commandId = Guid.NewGuid();
        Assert.IsNotNull(await fixture.Repository.CreateCollectServiceLogsCommandAsync(commandId, nodeId,
            service.Service!.Id, fixture.Time.GetUtcNow().AddMinutes(2), CancellationToken.None));
        var startedAt = fixture.Time.GetUtcNow();
        var succeeded = new AgentCommandResult(commandId, AgentCommandStatus.Succeeded, startedAt,
            startedAt.AddSeconds(1), null, null, "stdout: ready");

        Assert.IsTrue(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "1.0.0", "linux-x64", 0, null, [],
            [succeeded], CancellationToken.None));

        var diagnostics = await fixture.Repository.GetServiceDiagnosticsAsync(nodeId, service.Service.Id,
            CancellationToken.None);
        Assert.AreEqual(1, diagnostics.Count);
        Assert.AreEqual("Succeeded", diagnostics[0].Status);
        Assert.AreEqual("stdout: ready", diagnostics[0].Output);

        // A health-check command must never accept a success payload.
        var healthId = Guid.NewGuid();
        Assert.IsNotNull(await fixture.Repository.CreateRunHealthCheckCommandAsync(healthId, agentId,
            fixture.Time.GetUtcNow().AddMinutes(5), CancellationToken.None));
        var forged = new AgentCommandResult(healthId, AgentCommandStatus.Succeeded, startedAt,
            startedAt.AddSeconds(1), null, null, "not allowed");
        Assert.IsFalse(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "1.0.0", "linux-x64", 0, null, [],
            [forged], CancellationToken.None));
        var untouched = await fixture.Repository.GetAgentCommandAsync(healthId, CancellationToken.None);
        Assert.AreEqual("Pending", untouched!.Status);
    }

    [TestMethod]
    public async Task Diagnostics_TerminalRetention_KeepsOnlyTwentyMostRecent()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var agentId = await CreateAgentAsync(fixture, nodeId, "retention-token", "retention-secret");
        var service = await fixture.Repository.CreateServiceAsync(CreateService(nodeId, Guid.NewGuid(), "retention"),
            CancellationToken.None);

        for (var index = 0; index < 25; index++)
        {
            var commandId = Guid.NewGuid();
            Assert.IsNotNull(await fixture.Repository.CreateCollectServiceLogsCommandAsync(commandId, nodeId,
                service.Service!.Id, fixture.Time.GetUtcNow().AddMinutes(2), CancellationToken.None));
            var startedAt = fixture.Time.GetUtcNow();
            Assert.IsTrue(await fixture.Repository.TryUpdateAgentSyncAsync(agentId, "1.0.0", "linux-x64", 0, null, [],
                [new AgentCommandResult(commandId, AgentCommandStatus.Succeeded, startedAt, startedAt, null, null,
                    $"run {index}")], CancellationToken.None));
        }

        var diagnostics = await fixture.Repository.GetServiceDiagnosticsAsync(nodeId, service.Service!.Id,
            CancellationToken.None);
        Assert.AreEqual(20, diagnostics.Count);
        Assert.IsTrue(diagnostics.Any(record => record.Output == "run 24"));
        await using var connection = await fixture.OpenConnectionAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM agent_commands WHERE target_service_id = @service AND type = 'CollectServiceLogs' AND status IN ('Succeeded', 'Failed');";
        count.Parameters.AddWithValue("@service", service.Service.Id.ToString("D"));
        Assert.AreEqual(20L, (long)(await count.ExecuteScalarAsync())!);
    }

    [TestMethod]
    public async Task DeleteService_WithDiagnosticCommand_RemovesCommandAndServiceAtomically()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        await CreateAgentAsync(fixture, nodeId, "delete-diagnostic-token", "delete-diagnostic-secret");
        var service = await fixture.Repository.CreateServiceAsync(CreateService(nodeId, Guid.NewGuid(), "delete logs"),
            CancellationToken.None);
        var commandId = Guid.NewGuid();
        Assert.IsNotNull(await fixture.Repository.CreateCollectServiceLogsCommandAsync(commandId, nodeId,
            service.Service!.Id, fixture.Time.GetUtcNow().AddMinutes(2), CancellationToken.None));

        var revision = await fixture.Repository.DeleteServiceAsync(nodeId, service.Service.Id, CancellationToken.None);

        Assert.IsNotNull(revision);
        Assert.IsNull(await fixture.Repository.GetAgentCommandAsync(commandId, CancellationToken.None));
        Assert.AreEqual(0, (await fixture.Repository.GetServicesForNodeAsync(nodeId, CancellationToken.None)).Count);
    }

    [TestMethod]
    public void DiagnosticEffectiveStatus_ExpiredActiveCommand_IsExpiredButTerminalStatusIsPreserved()
    {
        var now = DateTimeOffset.Parse("2026-09-11T12:00:00+00:00");
        var pending = new AgentCommandRecord(Guid.NewGuid(), Guid.NewGuid(), "CollectServiceLogs", "Pending", now.AddMinutes(-3),
            null, null, null, null, now.AddMinutes(-1), Guid.NewGuid(), null);
        var succeeded = pending with { Status = "Succeeded", CompletedAtUtc = now.AddMinutes(-2) };

        Assert.AreEqual("Expired", AdminDiagnosticsEndpoints.EffectiveStatus(pending, now));
        Assert.AreEqual("Succeeded", AdminDiagnosticsEndpoints.EffectiveStatus(succeeded, now));
    }

    private static ServiceInstanceRecord CreateService(Guid nodeId, Guid id, string name) => new(
        id, nodeId, name, "hysteria2", "1.0", true, 2, "{\"port\":8443}",
        DateTimeOffset.Parse("2026-09-09T12:00:00+00:00"), DateTimeOffset.Parse("2026-09-09T12:00:00+00:00"));

    private static ServiceInstanceRecord XrayService(ServiceInstanceRecord service) => service with
    {
        BackendType = "xray",
        BackendVersion = "26.3.27",
        ConfigSchemaVersion = 1,
        ConfigJson = "{}"
    };

    private static async Task<Guid> CreateAgentAsync(TestDatabase fixture, string token, string secret) =>
        await CreateAgentAsync(fixture, Guid.NewGuid(), token, secret);

    private static async Task<Guid> CreateAgentAsync(TestDatabase fixture, Guid nodeId, string token, string secret)
    {
        var agentId = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(nodeId, nodeId.ToString("N"), CancellationToken.None);
        await fixture.Repository.CreateEnrollmentTokenAsync(Guid.NewGuid(), nodeId, token,
            fixture.Time.GetUtcNow().AddMinutes(5), CancellationToken.None);
        var enrollment = await fixture.Repository.TryConsumeEnrollmentTokenAndCreateAgentAsync(
            token, agentId, secret, "1.0.0", "linux-x64", CancellationToken.None);
        Assert.IsNotNull(enrollment);
        return agentId;
    }

    private static async Task<Guid> GetAgentIdAsync(TestDatabase fixture, Guid nodeId)
    {
        var observations = await fixture.Repository.GetNodeObservationsAsync(CancellationToken.None);
        return observations.Single(observation => observation.Id == nodeId).AgentId!.Value;
    }

    private static async Task<List<string>> ReadColumnNamesAsync(SqliteConnection connection, string tableName)
    {
        var result = new List<string>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) result.Add(reader.GetString(1));
        return result;
    }

    private static async Task<List<(string Table, string From, string To, string OnDelete)>> ReadForeignKeysAsync(
        SqliteConnection connection, string tableName)
    {
        var result = new List<(string Table, string From, string To, string OnDelete)>();
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA foreign_key_list(\"{tableName}\");";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add((reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(6)));
        return result;
    }

    private static async Task<List<string>> ReadAllTextValuesAsync(SqliteConnection connection)
    {
        var textValues = new List<string>();
        var tableNames = new List<string>();
        {
            await using var tablesCommand = connection.CreateCommand();
            tablesCommand.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
            await using var tables = await tablesCommand.ExecuteReaderAsync();
            while (await tables.ReadAsync())
            {
                tableNames.Add(tables.GetString(0));
            }
        }

        foreach (var tableName in tableNames)
        {
            var textColumns = new List<string>();
            {
                await using var columnsCommand = connection.CreateCommand();
                columnsCommand.CommandText = $"PRAGMA table_info(\"{tableName}\");";
                await using var columns = await columnsCommand.ExecuteReaderAsync();
                while (await columns.ReadAsync())
                {
                    if (columns.GetString(2).Equals("TEXT", StringComparison.OrdinalIgnoreCase))
                    {
                        textColumns.Add(columns.GetString(1));
                    }
                }
            }

            foreach (var columnName in textColumns)
            {
                await using var valuesCommand = connection.CreateCommand();
                valuesCommand.CommandText =
                    $"SELECT \"{columnName}\" FROM \"{tableName}\" WHERE \"{columnName}\" IS NOT NULL;";
                await using var values = await valuesCommand.ExecuteReaderAsync();
                while (await values.ReadAsync())
                {
                    textValues.Add(values.GetString(0));
                }
            }
        }

        return textValues;
    }

    private static async Task<byte[]> ReadBlobAsync(TestDatabase fixture, string sql, Guid id)
    {
        await using var connection = await fixture.OpenConnectionAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@id", id.ToString("D"));
        return (byte[])((await command.ExecuteScalarAsync())!);
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly string _directory;
        private readonly SqliteConnectionFactory _connectionFactory;

        private TestDatabase(string directory, FakeTimeProvider time, SqliteConnectionFactory connectionFactory)
        {
            _directory = directory;
            _connectionFactory = connectionFactory;
            Time = time;
            Migrations = new SqliteMigrationRunner(connectionFactory, time);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HyPanel:Security:MasterKey"] = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray())
            }).Build();
            Repository = new SqliteServerRepository(connectionFactory, time, new ProxyCredentialProtector(configuration));
        }

        public FakeTimeProvider Time { get; }

        public SqliteMigrationRunner Migrations { get; }

        public SqliteServerRepository Repository { get; }
        public string ConnectionString => $"Data Source={Path.Combine(_directory, "server.db")}";

        public static async Task<TestDatabase> CreateAsync()
        {
            var directory = Path.Combine(Path.GetTempPath(), "HyPanel.Server.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var databasePath = Path.Combine(directory, "server.db");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:HyPanel"] = $"Data Source={databasePath}",
                })
                .Build();
            var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T12:00:00+00:00"));
            var fixture = new TestDatabase(directory, time, new SqliteConnectionFactory(configuration));
            await fixture.Migrations.MigrateAsync(CancellationToken.None);
            return fixture;
        }

        public Task<SqliteConnection> OpenConnectionAsync() => _connectionFactory.OpenAsync(CancellationToken.None);

        public ValueTask DisposeAsync()
        {
            Directory.Delete(_directory, recursive: true);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow = _utcNow.Add(value);
    }
}
