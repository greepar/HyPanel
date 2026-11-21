using HyPanel.Shared.Contracts;

namespace HyPanel.Agent;

public interface IEnrollmentClient
{
    Task<AgentEnrollmentResponse> EnrollAsync(
        Uri panelBaseUri,
        AgentEnrollmentRequest request,
        CancellationToken cancellationToken);
}
