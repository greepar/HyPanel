namespace HyPanel.Server.Endpoints;

using HyPanel.Server.Persistence;
using HyPanel.Shared.Contracts;
using HyPanel.Shared.Serialization;

internal static class AgentEnrollmentEndpoints
{
    private const int MaximumTokenLength = 256;
    private const int MaximumAgentVersionLength = 128;
    private const int MaximumPlatformLength = 128;
    private const int MaximumHostnameLength = 255;
    private const int SyncIntervalSeconds = 8;

    public static void Map(IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/agent/v1/enroll", EnrollAsync);
    }

    private static async Task<IResult> EnrollAsync(
        AgentEnrollmentRequest request,
        EnrollmentService enrollmentService,
        CancellationToken cancellationToken)
    {
        if (!IsValidRequest(request))
        {
            return Results.BadRequest();
        }

        var enrollment = await enrollmentService.EnrollAsync(
            request.Token,
            request.AgentVersion.Trim(),
            request.Platform.Trim(),
            cancellationToken);
        if (enrollment is null)
        {
            return Results.Unauthorized();
        }

        var response = new AgentEnrollmentResponse(
            enrollment.Value.Enrollment.AgentId,
            enrollment.Value.AgentSecret,
            enrollment.Value.Enrollment.NodeId,
            SyncIntervalSeconds);
        return Results.Json(response, HyPanelJsonSerializerContext.Default.AgentEnrollmentResponse);
    }

    private static bool IsValidRequest(AgentEnrollmentRequest request) =>
        AdminNodesEndpoints.TryNormalize(request.Token, MaximumTokenLength, out _)
        && AdminNodesEndpoints.TryNormalize(request.AgentVersion, MaximumAgentVersionLength, out _)
        && AdminNodesEndpoints.TryNormalize(request.Platform, MaximumPlatformLength, out _)
        && request.Machine is not null
        && AdminNodesEndpoints.TryNormalize(request.Machine.Hostname, MaximumHostnameLength, out _);
}
