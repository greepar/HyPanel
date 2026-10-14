namespace HyPanel.Agent;

using System.Text;
using System.Text.Json;
using HyPanel.Agent.Backends.Infrastructure;

public sealed class ServiceLogCollector(BackendProcessSupervisor supervisor, BackendInstanceStore instanceStore)
{
    internal const int MaximumOutputBytes = 65536;

    public async Task<string?> CollectAsync(Guid serviceId, CancellationToken cancellationToken)
    {
        var metadata = await instanceStore.TryLoadAsync(serviceId, cancellationToken);
        if (metadata is null) return null;

        var secrets = ReadSecrets(metadata.DesiredState.ConfigJson);
        var entries = supervisor.GetStatus(serviceId).RecentLogs.Select(entry => Redact(entry, secrets));
        return CompleteEntryTail(entries, MaximumOutputBytes);
    }

    public static IReadOnlyList<string> ReadSecrets(string configJson)
    {
        using var document = JsonDocument.Parse(configJson);
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        Visit(document.RootElement, secrets);
        return secrets.OrderByDescending(static value => value.Length).ToArray();
    }

    private static void Visit(JsonElement element, HashSet<string> secrets)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.String && IsSecretName(property.Name) &&
                    property.Value.GetString() is { Length: >= 4 } value)
                    secrets.Add(value);
                else Visit(property.Value, secrets);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) Visit(child, secrets);
    }

    private static bool IsSecretName(string name) =>
        name.EndsWith("password", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("privateKey", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("secret", StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith("token", StringComparison.OrdinalIgnoreCase);

    public static string TailUtf8(string value, int maximumBytes)
    {
        if (Encoding.UTF8.GetByteCount(value) <= maximumBytes) return value;
        var start = value.Length;
        var byteCount = 0;
        while (start > 0)
        {
            var previous = start - 1;
            if (previous > 0 && char.IsLowSurrogate(value[previous]) && char.IsHighSurrogate(value[previous - 1]))
                previous--;
            var bytes = Encoding.UTF8.GetByteCount(value.AsSpan(previous, start - previous));
            if (byteCount + bytes > maximumBytes) break;
            byteCount += bytes;
            start = previous;
        }

        return value[start..];
    }

    public static string CompleteEntryTail(IEnumerable<string> entries, int maximumBytes)
    {
        var selected = new List<string>();
        var bytes = 0;
        foreach (var entry in entries.Reverse())
        {
            var entryBytes = Encoding.UTF8.GetByteCount(entry);
            var separatorBytes = selected.Count == 0 ? 0 : 1;
            if (entryBytes + separatorBytes > maximumBytes - bytes) break;
            selected.Add(entry);
            bytes += entryBytes + separatorBytes;
        }
        selected.Reverse();
        return string.Join('\n', selected);
    }

    private static string Redact(string value, IReadOnlyList<string> secrets)
    {
        foreach (var secret in secrets) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        return value;
    }
}
