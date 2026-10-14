namespace HyPanel.Shared.Contracts;

public sealed record AgentReleaseManifest(
    int SchemaVersion,
    string Version,
    DateTimeOffset PublishedAt,
    IReadOnlyList<AgentReleaseAsset> Assets);

public sealed record AgentReleaseAsset(
    string Rid,
    string FileName,
    string Sha256,
    long Size);
