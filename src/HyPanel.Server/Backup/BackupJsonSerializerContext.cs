using System.Text.Json.Serialization;

namespace HyPanel.Server.Backup;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(PendingRestore))]
[JsonSerializable(typeof(RestoreStatus))]
internal sealed partial class BackupJsonSerializerContext : JsonSerializerContext;
