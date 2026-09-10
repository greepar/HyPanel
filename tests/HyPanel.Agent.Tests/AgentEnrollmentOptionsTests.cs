using HyPanel.Agent;
using Microsoft.Extensions.Configuration;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace HyPanel.Agent.Tests;

[TestClass]
public sealed class AgentEnrollmentOptionsTests
{
    [TestMethod]
    public void FromConfiguration_ReadsExplicitDataDirectoryWithoutEnvironmentMutation()
    {
        const string dataDirectory = "/tmp/hypanel-agent-test-data";
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["HYPANEL_DATA_DIR"] = dataDirectory,
                ["HYPANEL_PANEL_URL"] = "https://panel.example",
                ["HYPANEL_ENROLLMENT_TOKEN"] = "enrollment-token",
            })
            .Build();

        var options = AgentEnrollmentOptions.FromConfiguration(configuration);

        Assert.AreEqual(dataDirectory, options.DataDirectory);
        Assert.AreEqual("https://panel.example", options.PanelUrl);
        Assert.AreEqual("enrollment-token", options.EnrollmentToken);
    }
}
