namespace HyPanel.Server.Backup;

using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(BackupManifest))]
[JsonSerializable(typeof(PendingRestore))]
[JsonSerializable(typeof(RestoreStatus))]
internal sealed partial class BackupJsonSerializerContext : JsonSerializerContext;
