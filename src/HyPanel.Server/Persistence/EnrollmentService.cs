namespace HyPanel.Server.Persistence;

using System.Security.Cryptography;

internal sealed class EnrollmentService(SqliteServerRepository repository, TimeProvider timeProvider)
{
    private const int TokenByteLength = 32;
    private const int SecretByteLength = 32;

    public Task<EnrollmentTokenIssue> IssueTokenAsync(Guid nodeId, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenByteLength)).ToLowerInvariant();
        return repository.CreateEnrollmentTokenAsync(Guid.NewGuid(), nodeId, token, timeProvider.GetUtcNow().Add(lifetime), cancellationToken);
    }

    public async Task<(AgentEnrollmentResult Enrollment, string AgentSecret)?> EnrollAsync(
        string token,
        string agentVersion,
        string platform,
        CancellationToken cancellationToken)
    {
        var agentSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(SecretByteLength)).ToLowerInvariant();
        var enrollment = await repository.TryConsumeEnrollmentTokenAndCreateAgentAsync(
            token,
            Guid.NewGuid(),
            agentSecret,
            agentVersion,
            platform,
            cancellationToken);
        return enrollment is null ? null : (enrollment, agentSecret);
    }
}
