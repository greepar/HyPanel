namespace HyPanel.Agent;

using System.Net.Http.Headers;
using System.Text.Json;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;

public sealed class EnrollmentClient(HttpClient httpClient) : IEnrollmentClient
{
    private static readonly Uri EnrollmentUri = new("/api/agent/v1/enroll", UriKind.Relative);

    public async Task<AgentEnrollmentResponse> EnrollAsync(
        Uri panelBaseUri,
        AgentEnrollmentRequest request,
        CancellationToken cancellationToken)
    {
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, new Uri(panelBaseUri, EnrollmentUri));
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(
            request,
            HyPanelJsonSerializerContext.Default.AgentEnrollmentRequest);
        requestMessage.Content = new ByteArrayContent(requestBytes);
        requestMessage.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");

        using var response = await httpClient.SendAsync(requestMessage, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return JsonSerializer.Deserialize(
                   responseBytes,
                   HyPanelJsonSerializerContext.Default.AgentEnrollmentResponse)
               ?? throw new HttpRequestException("The enrollment response was empty or invalid.");
    }
}
