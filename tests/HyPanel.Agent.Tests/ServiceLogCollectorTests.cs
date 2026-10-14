using System.Text;
using HyPanel.Agent;
using HyPanel.Agent.Backends;
using HyPanel.Agent.Backends.Infrastructure;
using HyPanel.Shared.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class ServiceLogCollectorTests
{
    [TestMethod]
    public void TailUtf8_UnicodeInput_ReturnsValidNewestContentWithinByteLimit()
    {
        var result = ServiceLogCollector.TailUtf8("prefix-秘密-😀-tail", 12);

        Assert.IsTrue(Encoding.UTF8.GetByteCount(result) <= 12);
        StringAssert.EndsWith(result, "-tail");
        Assert.IsFalse(result.Contains('\uFFFD'));
        Assert.IsFalse(result.Contains("prefix"));
    }

    [TestMethod]
    public void TailUtf8_ContentWithinLimit_IsReturnedUnchanged()
    {
        Assert.AreEqual("short", ServiceLogCollector.TailUtf8("short", 1024));
    }

    [TestMethod]
    public void CompleteEntryTail_StopsAtEntryBoundaryAndStaysWithinUtf8Limit()
    {
        var entries = new[] { "oldest", new string('中', 30_000), "newest" };

        var result = ServiceLogCollector.CompleteEntryTail(entries, 65_536);

        Assert.AreEqual("newest", result);
        Assert.IsTrue(Encoding.UTF8.GetByteCount(result) <= 65_536);
        Assert.IsFalse(result.Contains('中'));
    }

    [TestMethod]
    public void ReadSecrets_NestedConfiguration_ReturnsOnlyNamedSecretsLongestFirst()
    {
        var secrets = ServiceLogCollector.ReadSecrets(
            "{\"authPassword\":\"long-secret\",\"nested\":{\"token\":\"abcd\",\"host\":\"public.example\"}}");

        CollectionAssert.AreEqual(new[] { "long-secret", "abcd" }, secrets.ToArray());
    }

    [TestMethod]
    public void ReadSecrets_ShortOrUnnamedValues_AreIgnored()
    {
        var secrets = ServiceLogCollector.ReadSecrets("{\"authPassword\":\"abc\",\"host\":\"example.com\"}");

        Assert.AreEqual(0, secrets.Count);
    }

    [TestMethod]
    public async Task CollectAsync_UnmanagedService_ReturnsNullWithoutReadingFilesystem()
    {
        var directory = Path.Combine(Path.GetTempPath(), "HyPanel.Agent.Tests", Guid.NewGuid().ToString("N"));
        var options = new AgentEnrollmentOptions(null, null, directory);
        await using var supervisor = new BackendProcessSupervisor(NullLogger<BackendProcessSupervisor>.Instance);
        var collector = new ServiceLogCollector(supervisor, new BackendInstanceStore(options));

        var result = await collector.CollectAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task CollectAsync_ManagedProcessOutput_RedactsAppliedSecret()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This collector assertion uses the POSIX shell and is covered on Unix runners.");
        }

        var directory = Path.Combine(Path.GetTempPath(), "HyPanel.Agent.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new AgentEnrollmentOptions(null, null, directory);
            var instanceStore = new BackendInstanceStore(options);
            var serviceId = Guid.NewGuid();
            var desired = new ServiceDesiredState(serviceId, "logs", "fake", "1.0.0", true, 1,
                "{\"authPassword\":\"do-not-leak\"}");
            var content = Encoding.UTF8.GetBytes("{}");
            await instanceStore.SaveConfigAsync(desired,
                new RenderedBackendConfig("config.json", content,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant()),
                CancellationToken.None);

            await using var supervisor = new BackendProcessSupervisor(NullLogger<BackendProcessSupervisor>.Instance);
            await supervisor.StartAsync(serviceId,
                new BackendProcessSpec("/bin/sh", ["-c", "printf 'visible do-not-leak\\n'; sleep 5"],
                    instanceStore.GetInstanceDirectory(serviceId), new Dictionary<string, string>()),
                CancellationToken.None);
            for (var attempt = 0; attempt < 100 && supervisor.GetStatus(serviceId).RecentLogs.Count == 0; attempt++)
            {
                await Task.Delay(20);
            }

            var result = await new ServiceLogCollector(supervisor, instanceStore)
                .CollectAsync(serviceId, CancellationToken.None);

            Assert.IsNotNull(result);
            StringAssert.Contains(result, "visible [REDACTED]");
            Assert.IsFalse(result.Contains("do-not-leak", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task CollectAsync_EmptyLogBuffer_ReturnsEmptyStringNotError()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("This collector assertion uses the POSIX shell and is covered on Unix runners.");
        }

        var directory = Path.Combine(Path.GetTempPath(), "HyPanel.Agent.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var options = new AgentEnrollmentOptions(null, null, directory);
            var instanceStore = new BackendInstanceStore(options);
            var serviceId = Guid.NewGuid();
            var desired = new ServiceDesiredState(serviceId, "quiet", "fake", "1.0.0", true, 1, "{}");
            var content = Encoding.UTF8.GetBytes("{}");
            await instanceStore.SaveConfigAsync(desired,
                new RenderedBackendConfig("config.json", content,
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content)).ToLowerInvariant()),
                CancellationToken.None);

            await using var supervisor = new BackendProcessSupervisor(NullLogger<BackendProcessSupervisor>.Instance);
            var result = await new ServiceLogCollector(supervisor, instanceStore)
                .CollectAsync(serviceId, CancellationToken.None);

            Assert.AreEqual(string.Empty, result);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ProcessOutput_LineExceedingLimitWithoutNewline_IsOmittedBeforeStorage()
    {
        if (OperatingSystem.IsWindows()) Assert.Inconclusive("Uses the POSIX shell on Unix runners.");
        var directory = Path.Combine(Path.GetTempPath(), "HyPanel.Agent.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = "/bin/sh";
            var serviceId = Guid.NewGuid();
            await using var supervisor = new BackendProcessSupervisor(NullLogger<BackendProcessSupervisor>.Instance);
            await supervisor.StartAsync(serviceId,
                new BackendProcessSpec(executable, ["-c", "printf '%05000dpartial-secret' 0"], directory,
                    new Dictionary<string, string>()), CancellationToken.None);
            for (var attempt = 0; attempt < 100 && supervisor.GetStatus(serviceId).RecentLogs.Count == 0; attempt++)
                await Task.Delay(20);

            var logs = supervisor.GetStatus(serviceId).RecentLogs;
            Assert.AreEqual(1, logs.Count);
            Assert.AreEqual("stdout: [line omitted: exceeded limit]", logs[0]);
            Assert.IsFalse(logs[0].Contains("partial-secret", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
