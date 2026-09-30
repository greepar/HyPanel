namespace HyPanel.Shared.Contracts;

public sealed record AgentReleaseManifest(
    int SchemaVersion,
    string Version,
    DateTimeOffset PublishedAt,
    IReadOnlyList<AgentReleaseAsset> Assets,
    IReadOnlyList<BackendArtifact>? Tools = null);

public sealed record AgentReleaseAsset(
    string Rid,
    string FileName,
    string Sha256,
    long Size);
