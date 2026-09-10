namespace HyPanel.Agent;

using HyPanel.Shared.Contracts;

public interface IEnrollmentClient
{
    Task<AgentEnrollmentResponse> EnrollAsync(
        Uri panelBaseUri,
        AgentEnrollmentRequest request,
        CancellationToken cancellationToken);
}
