using HyPanel.Server.Persistence;
using HyPanel.Server.Endpoints;
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
            new List<(long Version, long Count)> { (1L, 1L), (2L, 1L), (3L, 1L), (4L, 1L), (5L, 1L), (6L, 1L) },
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
        CollectionAssert.AreEquivalent(new[]
            {
                "id", "name", "normalized_name", "backend_type", "backend_version", "config_schema_version",
                "config_json", "created_at_utc", "updated_at_utc"
            },
            await ReadColumnNamesAsync(connection, "service_templates"));
    }

    [TestMethod]
    public async Task Templates_InstantiateAndBatchEnabled_IncrementAffectedNodesOnce()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeOne = Guid.NewGuid();
        var nodeTwo = Guid.NewGuid();
        await fixture.Repository.CreateNodeAsync(nodeOne, "one", CancellationToken.None);
        await fixture.Repository.CreateNodeAsync(nodeTwo, "two", CancellationToken.None);
        var now = fixture.Time.GetUtcNow();
        var template = new ServiceTemplateRecord(Guid.NewGuid(), " Template ", "template", "xray", "1.0", 1,
            "{\"password\":\"secret\"}", now, now);
        Assert.IsTrue(await fixture.Repository.CreateServiceTemplateAsync(template, CancellationToken.None));
        Assert.IsFalse(await fixture.Repository.CreateServiceTemplateAsync(template with { Id = Guid.NewGuid() },
            CancellationToken.None));

        var instantiated =
            await fixture.Repository.CreateServiceFromTemplateAsync(nodeOne, template.Id, "from template",
                CancellationToken.None);
        Assert.IsNotNull(instantiated.Service);
        Assert.AreEqual(1L, instantiated.Revision);
        Assert.AreEqual("xray", instantiated.Service.BackendType);
        var second = await fixture.Repository.CreateServiceAsync(CreateService(nodeTwo, Guid.NewGuid(), "second"),
            CancellationToken.None);
        var third = await fixture.Repository.CreateServiceAsync(CreateService(nodeOne, Guid.NewGuid(), "third"),
            CancellationToken.None);

        var result = await fixture.Repository.SetServicesEnabledBatchAsync(new[]
        {
            new BatchServiceEnabledItemRecord(nodeOne, instantiated.Service.Id, false),
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
            new BatchServiceEnabledItemRecord(nodeOne, instantiated.Service.Id, true),
            new BatchServiceEnabledItemRecord(nodeOne, instantiated.Service.Id, false)
        }, CancellationToken.None));
        Assert.IsFalse((await fixture.Repository.GetServicesForNodeAsync(nodeOne, CancellationToken.None))
            .Single(item => item.Service.Id == instantiated.Service.Id).Service.Enabled);

        var updated = template with
        {
            Name = "Renamed",
            NormalizedName = "renamed",
            ConfigJson = "{\"password\":\"changed\"}",
            UpdatedAtUtc = now.AddMinutes(1)
        };
        Assert.IsTrue(await fixture.Repository.UpdateServiceTemplateAsync(updated, CancellationToken.None));
        Assert.AreEqual("Renamed",
            (await fixture.Repository.GetServiceTemplateAsync(template.Id, CancellationToken.None))!.Name);
        Assert.IsTrue(await fixture.Repository.DeleteServiceTemplateAsync(template.Id, CancellationToken.None));
        Assert.IsNull(await fixture.Repository.GetServiceTemplateAsync(template.Id, CancellationToken.None));
    }

    [TestMethod]
    public void ServiceTemplateValidation_NormalizesNameAndRequiresSchemaVersionOne()
    {
        Assert.IsTrue(AdminServiceTemplateEndpoints.TryValidate(
            "  ＴＥＭＰＬＡＴＥ  ", "MIHOMO", " 1.19.30 ", 1, "{ \"password\": \"secret\" }",
            out var name, out var normalizedName, out var backendType, out var version, out var config));
        Assert.AreEqual("TEMPLATE", name);
        Assert.AreEqual("template", normalizedName);
        Assert.AreEqual("mihomo", backendType);
        Assert.AreEqual("1.19.30", version);
        Assert.AreEqual("{ \"password\": \"secret\" }", config);
        StringAssert.Contains(AdminServicesEndpoints.RedactPasswords(config), "[REDACTED]");

        Assert.IsFalse(AdminServiceTemplateEndpoints.TryValidate(
            "template", "mihomo", "1.19.30", 2, "{}", out _, out _, out _, out _, out _));
    }

    [TestMethod]
    public void HealthSummaryBuild_ClassifiesOnlineDriftOfflineAndFailedServices()
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
                "config_json", "created_at_utc", "updated_at_utc"
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
        var service = CreateService(nodeId, Guid.NewGuid(), "initial");

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
        Assert.AreEqual(3L, deleteRevision);
        Assert.AreEqual(0, (await fixture.Repository.GetServicesForNodeAsync(nodeId, CancellationToken.None)).Count);
        Assert.AreEqual(0,
            (await fixture.Repository.GetBoundServicesAsync(user.User.Id, CancellationToken.None)).Count);
        Assert.IsNull(await fixture.Repository.GetServicePublicEndpointAsync(nodeId, service.Id,
            CancellationToken.None));
    }

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
        var service = CreateService(nodeId, Guid.NewGuid(), "bound");
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
    public async Task PublicSubscription_RequiresEligibleRotatedTokenAndProjectsOnlyClientFieldsInAllFormats()
    {
        await using var fixture = await TestDatabase.CreateAsync();
        var nodeId = Guid.NewGuid();
        var service = CreateService(nodeId, Guid.NewGuid(), "My service") with
        {
            ConfigJson =
            "{\"authPassword\":\"auth secret\",\"obfsPassword\":\"obfs secret\",\"certificatePath\":\"/private/cert\",\"masqueradeUrl\":\"https://private.example\",\"upMbps\":100}"
        };
        await fixture.Repository.CreateNodeAsync(nodeId, "node", CancellationToken.None);
        await fixture.Repository.CreateServiceAsync(service, CancellationToken.None);
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Sub", "sub", "password-hash", "User", true,
            100, null, "old-token", CancellationToken.None);
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, service.Id, CancellationToken.None));
        var endpoint =
            new ServicePublicEndpointRecord(service.Id, "sub.example", 443, "sni.example", fixture.Time.GetUtcNow());
        Assert.IsTrue(await fixture.Repository.SetServicePublicEndpointAsync(nodeId, endpoint, CancellationToken.None));
        Assert.IsFalse(
            await fixture.Repository.SetServicePublicEndpointAsync(Guid.NewGuid(), endpoint, CancellationToken.None));
        Assert.AreEqual(endpoint,
            await fixture.Repository.GetServicePublicEndpointAsync(nodeId, service.Id, CancellationToken.None));
        await AssertSubscriptionStatusAsync(fixture, "unknown-token", StatusCodes.Status404NotFound);

        foreach (var format in new[] { "raw", "base64", "mihomo", "singbox" })
        {
            var context = new DefaultHttpContext();
            context.Response.Body = new MemoryStream();
            await SubscriptionEndpoints.GetAsync("old-token", format, context.Response, fixture.Repository,
                CancellationToken.None);
            Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.AreEqual("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.AreEqual("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
            context.Response.Body.Position = 0;
            var content = await new StreamReader(context.Response.Body).ReadToEndAsync();
            if (format == "base64") content = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(content));
            StringAssert.Contains(content, format is "raw" or "base64" ? "auth%20secret" : "auth secret");
            Assert.IsFalse(content.Contains("/private/cert", StringComparison.Ordinal));
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
        await SubscriptionEndpoints.GetAsync("empty-token", "raw", emptyContext.Response, fixture.Repository,
            CancellationToken.None);
        Assert.AreEqual(StatusCodes.Status200OK, emptyContext.Response.StatusCode);
        Assert.AreEqual(0, emptyContext.Response.Body.Length);
        Assert.IsNotNull(empty.User);
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
        const string clientId = "01234567-89ab-cdef-0123-456789abcdef";
        var xray = CreateService(nodeId, Guid.NewGuid(), "xray # one") with
        {
            BackendType = "xray",
            BackendVersion = "26.3.27",
            ConfigSchemaVersion = 1,
            ConfigJson =
            $$"""{"listenHost":"0.0.0.0","listenPort":24445,"clientId":"{{clientId}}","clientEmail":"client@example","flow":"xtls-rprx-vision","realityPrivateKey":"{{privateKey}}","realityPublicKey":"{{publicKey}}","shortId":"a1b2","serverName":"www.example.com","destination":"private-destination.example:443","fingerprint":"chrome"}"""
        };
        await fixture.Repository.CreateServiceAsync(hysteria, CancellationToken.None);
        await fixture.Repository.CreateServiceAsync(xray, CancellationToken.None);
        var user = await fixture.Repository.CreateUserAsync(Guid.NewGuid(), "Mixed", "mixed", "hash", "User", true,
            null, null, "mixed-token", CancellationToken.None);
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, hysteria.Id, CancellationToken.None));
        Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, xray.Id, CancellationToken.None));
        Assert.IsTrue(await fixture.Repository.SetServicePublicEndpointAsync(nodeId,
            new ServicePublicEndpointRecord(hysteria.Id, "hy.example", 24444, "hy.example", fixture.Time.GetUtcNow()),
            CancellationToken.None));
        Assert.IsTrue(await fixture.Repository.SetServicePublicEndpointAsync(nodeId,
            new ServicePublicEndpointRecord(xray.Id, "2001:db8::10", 24445, null, fixture.Time.GetUtcNow()),
            CancellationToken.None));

        var raw = await RenderSubscriptionAsync(fixture, "mixed-token", "raw");
        var expectedVless =
            $"vless://{clientId}@[2001:db8::10]:24445?encryption=none&flow=xtls-rprx-vision&security=reality&sni=www.example.com&fp=chrome&pbk=public-key-value&sid=a1b2&type=tcp#xray%20%23%20one";
        StringAssert.Contains(raw, "hysteria2://hy-secret@hy.example:24444/");
        StringAssert.Contains(raw, expectedVless);
        Assert.AreEqual(raw, Encoding.UTF8.GetString(Convert.FromBase64String(
            await RenderSubscriptionAsync(fixture, "mixed-token", "base64"))));

        var mihomo = await RenderSubscriptionAsync(fixture, "mixed-token", "mihomo");
        StringAssert.Contains(mihomo, "type: hysteria2");
        StringAssert.Contains(mihomo, "type: vless");
        StringAssert.Contains(mihomo, "public-key: \"public-key-value\"");
        StringAssert.Contains(mihomo, "short-id: \"a1b2\"");
        var singbox = await RenderSubscriptionAsync(fixture, "mixed-token", "singbox");
        using var document = JsonDocument.Parse(singbox);
        Assert.AreEqual(2, document.RootElement.GetProperty("outbounds").GetArrayLength());
        Assert.AreEqual("public-key-value", document.RootElement.GetProperty("outbounds")[1]
            .GetProperty("tls").GetProperty("reality").GetProperty("public_key").GetString());

        foreach (var output in new[] { raw, mihomo, singbox })
        {
            Assert.IsFalse(output.Contains(privateKey, StringComparison.Ordinal));
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
            Assert.IsTrue(await fixture.Repository.BindServiceAsync(user.User.Id, service.Id, CancellationToken.None));
            Assert.IsTrue(await fixture.Repository.SetServicePublicEndpointAsync(nodeId,
                new ServicePublicEndpointRecord(service.Id, service == mihomo ? "mihomo.example" : "singbox.example",
                    service == mihomo ? 443 : 8443, null, fixture.Time.GetUtcNow()), CancellationToken.None));
        }

        var raw = await RenderSubscriptionAsync(fixture, "ss-token", "raw");
        var expectedMihomo =
            $"ss://{Base64Url($"chacha20-ietf-poly1305:{mihomoPassword}")}@mihomo.example:443#mihomo%20ss";
        var expectedSingBox =
            $"ss://{Base64Url($"2022-blake3-aes-256-gcm:{singBoxPassword}")}@singbox.example:8443#sing-box%20ss";
        StringAssert.Contains(raw, expectedMihomo);
        StringAssert.Contains(raw, expectedSingBox);
        Assert.AreEqual(2, raw.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        foreach (var uri in raw.Split('\n'))
        {
            var userInfo = uri[5..uri.IndexOf('@')];
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(
                userInfo.Replace('-', '+').Replace('_', '/') + new string('=', (4 - userInfo.Length % 4) % 4)));
            StringAssert.Contains(decoded, ":");
        }

        Assert.AreEqual(raw, Encoding.UTF8.GetString(Convert.FromBase64String(
            await RenderSubscriptionAsync(fixture, "ss-token", "base64"))));

        var mihomoOutput = await RenderSubscriptionAsync(fixture, "ss-token", "mihomo");
        StringAssert.Contains(mihomoOutput, "type: ss");
        StringAssert.Contains(mihomoOutput, "    cipher: \"chacha20-ietf-poly1305\"");
        StringAssert.Contains(mihomoOutput, $"    password: \"{mihomoPassword}\"");
        StringAssert.Contains(mihomoOutput, "    cipher: \"2022-blake3-aes-256-gcm\"");
        StringAssert.Contains(mihomoOutput, $"    password: \"{singBoxPassword}\"");
        Assert.AreEqual(2, mihomoOutput.Split("type: ss", StringSplitOptions.None).Length - 1);
        Assert.AreEqual(2, mihomoOutput.Split("    udp: true", StringSplitOptions.None).Length - 1);

        using var singBoxDocument = JsonDocument.Parse(await RenderSubscriptionAsync(fixture, "ss-token", "singbox"));
        var outbounds = singBoxDocument.RootElement.GetProperty("outbounds");
        Assert.AreEqual(2, outbounds.GetArrayLength());
        AssertShadowboxOutbound(outbounds[0], "chacha20-ietf-poly1305", mihomoPassword, "mihomo.example", 443);
        AssertShadowboxOutbound(outbounds[1], "2022-blake3-aes-256-gcm", singBoxPassword, "singbox.example", 8443);

        var redactedMihomo = AdminServicesEndpoints.RedactPasswords(mihomo.ConfigJson);
        var redactedSingBox = AdminServicesEndpoints.RedactPasswords(singBox.ConfigJson);
        Assert.IsFalse(redactedMihomo.Contains(mihomoPassword, StringComparison.Ordinal));
        Assert.IsFalse(redactedSingBox.Contains(singBoxPassword, StringComparison.Ordinal));
        StringAssert.Contains(redactedMihomo, "[REDACTED]");
        StringAssert.Contains(redactedSingBox, "[REDACTED]");
    }

    private static void AssertShadowboxOutbound(JsonElement outbound, string method, string password, string host,
        int port)
    {
        Assert.AreEqual("shadowsocks", outbound.GetProperty("type").GetString());
        Assert.AreEqual(method, outbound.GetProperty("method").GetString());
        Assert.AreEqual(password, outbound.GetProperty("password").GetString());
        Assert.AreEqual(host, outbound.GetProperty("server").GetString());
        Assert.AreEqual(port, outbound.GetProperty("server_port").GetInt32());
    }

    private static string Base64Url(string value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string> RenderSubscriptionAsync(TestDatabase fixture, string token, string format)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await SubscriptionEndpoints.GetAsync(token, format, context.Response, fixture.Repository,
            CancellationToken.None);
        Assert.AreEqual(StatusCodes.Status200OK, context.Response.StatusCode);
        context.Response.Body.Position = 0;
        return await new StreamReader(context.Response.Body).ReadToEndAsync();
    }

    private static async Task AssertSubscriptionStatusAsync(TestDatabase fixture, string token, int statusCode)
    {
        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        await SubscriptionEndpoints.GetAsync(token, "raw", context.Response, fixture.Repository,
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
        var service = CreateService(nodeId, Guid.NewGuid(), "usage");
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
        var service = CreateService(nodeId, Guid.NewGuid(), "overflow");
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

    private static ServiceInstanceRecord CreateService(Guid nodeId, Guid id, string name) => new(
        id, nodeId, name, "hysteria2", "1.0", true, 2, "{\"port\":8443}",
        DateTimeOffset.Parse("2026-09-09T12:00:00+00:00"), DateTimeOffset.Parse("2026-09-09T12:00:00+00:00"));

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
            Repository = new SqliteServerRepository(connectionFactory, time);
        }

        public FakeTimeProvider Time { get; }

        public SqliteMigrationRunner Migrations { get; }

        public SqliteServerRepository Repository { get; }

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