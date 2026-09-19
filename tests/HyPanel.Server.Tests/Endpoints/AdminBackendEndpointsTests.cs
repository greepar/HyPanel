namespace HyPanel.Server.Tests.Endpoints;

using System.Text.Json;
using HyPanel.Server.Backends;
using HyPanel.Server.Endpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public sealed class AdminBackendEndpointsTests
{
    [TestMethod]
    public void BackendDefinitions_SerializeThroughSourceGeneratedContext()
    {
        var definitions = BackendDefinitionCatalog.All.Select(item => new BackendDefinitionResponse(
            item.BackendType, item.DisplayName, item.Core, item.Protocol, item.Description, item.Badge,
            item.FallbackVersion, item.Fields)).ToArray();

        var json = JsonSerializer.Serialize(definitions,
            ServerJsonSerializerContext.Default.BackendDefinitionResponseArray);

        StringAssert.Contains(json, "\"backendType\":\"hysteria2\"");
        StringAssert.Contains(json, "\"listenPort\"");
        StringAssert.Contains(json, "\"generate\":true");
        StringAssert.Contains(json, "\"fixed\":\"2022-blake3-aes-256-gcm\"");
    }

    [TestMethod]
    public void BackendDefaults_SerializeThroughSourceGeneratedContext()
    {
        var response = new BackendDefaultsResponse("xray", BackendDefinitionCatalog.GenerateDefaults("xray"));

        var json = JsonSerializer.Serialize(response, ServerJsonSerializerContext.Default.BackendDefaultsResponse);

        StringAssert.Contains(json, "\"backendType\":\"xray\"");
        StringAssert.Contains(json, "\"realityPrivateKey\"");
        StringAssert.Contains(json, "\"realityPublicKey\"");
    }
}
