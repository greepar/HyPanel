namespace HyPanel.Agent.Backends.Infrastructure;

using System.Text.Json;
using System.Text.Json.Serialization;
using HyPanel.Shared.Contracts;

public sealed record BackendInstanceMetadata(
    ServiceDesiredState DesiredState,
    string ConfigFileName,
    string ConfigSha256,
    DateTimeOffset UpdatedAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(BackendInstanceMetadata))]
internal sealed partial class BackendInfrastructureJsonSerializerContext : JsonSerializerContext;

public sealed class BackendInstanceStore(AgentEnrollmentOptions options)
{
    private const string MetadataFileName = "desired.json";
    private const string LastKnownGoodDirectory = "last-known-good";

    public string GetInstanceDirectory(Guid serviceId) => Path.Combine(ServicesRoot, serviceId.ToString("D"));

    public async Task<BackendInstanceMetadata?> TryLoadAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        var metadata = Path.Combine(GetInstanceDirectory(serviceId), MetadataFileName);
        if (!File.Exists(metadata)) return null;
        var bytes = await File.ReadAllBytesAsync(metadata, cancellationToken);
        return JsonSerializer.Deserialize(bytes,
                   BackendInfrastructureJsonSerializerContext.Default.BackendInstanceMetadata)
               ?? throw new InvalidDataException("Service desired-state metadata is invalid.");
    }

    public async Task<string> SaveConfigAsync(ServiceDesiredState desiredState, RenderedBackendConfig config,
        CancellationToken cancellationToken)
    {
        var directory = GetInstanceDirectory(desiredState.ServiceId);
        ValidateConfig(desiredState, config);
        Directory.CreateDirectory(directory);
        var configPath = ManagedPath(directory, config.FileName);
        await WriteAtomicAsync(directory, config.FileName, config.Content, cancellationToken);
        var metadata = new BackendInstanceMetadata(desiredState, config.FileName, config.Sha256, DateTimeOffset.UtcNow);
        await WriteMetadataAsync(directory, metadata, cancellationToken);
        return configPath;
    }

    public async Task SaveLastKnownGoodAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        var directory = GetInstanceDirectory(serviceId);
        var metadata = await TryLoadAsync(serviceId, cancellationToken) ??
                       throw new InvalidOperationException("No current service configuration exists.");
        var config = ManagedPath(directory, metadata.ConfigFileName);
        if (!File.Exists(config)) throw new FileNotFoundException("Current service config does not exist.", config);
        var snapshot = Path.Combine(directory, LastKnownGoodDirectory);
        Directory.CreateDirectory(snapshot);
        await WriteAtomicAsync(snapshot, metadata.ConfigFileName,
            await File.ReadAllBytesAsync(config, cancellationToken), cancellationToken);
        await WriteMetadataAsync(snapshot, metadata, cancellationToken);
    }

    public async Task<bool> TryRollbackAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        var directory = GetInstanceDirectory(serviceId);
        var snapshot = Path.Combine(directory, LastKnownGoodDirectory);
        var snapshotMetadataPath = Path.Combine(snapshot, MetadataFileName);
        if (!File.Exists(snapshotMetadataPath)) return false;
        var bytes = await File.ReadAllBytesAsync(snapshotMetadataPath, cancellationToken);
        var metadata = JsonSerializer.Deserialize(bytes,
            BackendInfrastructureJsonSerializerContext.Default.BackendInstanceMetadata);
        if (metadata is null || !IsSafeFileName(metadata.ConfigFileName)) return false;
        var snapshotConfig = ManagedPath(snapshot, metadata.ConfigFileName);
        if (!File.Exists(snapshotConfig)) return false;
        await WriteAtomicAsync(directory, metadata.ConfigFileName,
            await File.ReadAllBytesAsync(snapshotConfig, cancellationToken), cancellationToken);
        await WriteMetadataAsync(directory, metadata, cancellationToken);
        return true;
    }

    public void Delete(Guid serviceId)
    {
        var directory = GetInstanceDirectory(serviceId);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private string ServicesRoot => Path.Combine(options.DataDirectory, "services");

    private static void ValidateConfig(ServiceDesiredState desiredState, RenderedBackendConfig config)
    {
        if (desiredState.ServiceId == Guid.Empty || !IsSafeFileName(config.FileName) || config.Sha256.Length != 64 ||
            config.Sha256.Any(character => !(character is >= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new InvalidDataException("Rendered backend configuration is invalid.");
    }

    private static async Task WriteMetadataAsync(string directory, BackendInstanceMetadata metadata,
        CancellationToken cancellationToken) =>
        await WriteAtomicAsync(directory, MetadataFileName,
            JsonSerializer.SerializeToUtf8Bytes(metadata,
                BackendInfrastructureJsonSerializerContext.Default.BackendInstanceMetadata), cancellationToken);

    private static async Task WriteAtomicAsync(string directory, string name, ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken)
    {
        var destination = ManagedPath(directory, name);
        var temporary = ManagedPath(directory, $".{name}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, content, cancellationToken);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static bool IsSafeFileName(string value) => !string.IsNullOrWhiteSpace(value) &&
                                                        Path.GetFileName(value) == value && value.All(character =>
                                                            char.IsAsciiLetterOrDigit(character) ||
                                                            character is '.' or '-' or '_');

    private static string ManagedPath(string directory, string name)
    {
        var fullDirectory = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var path = Path.GetFullPath(Path.Combine(directory, name));
        return path.StartsWith(fullDirectory, StringComparison.Ordinal)
            ? path
            : throw new InvalidDataException("Path escapes service directory.");
    }
}