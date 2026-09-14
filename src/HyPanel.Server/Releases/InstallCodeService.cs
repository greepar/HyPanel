namespace HyPanel.Server.Releases;

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

internal sealed class InstallCodeService(TimeProvider timeProvider)
{
    private readonly ConcurrentDictionary<string, InstallCodeEntry> entries = new(StringComparer.Ordinal);

    public string Issue(string platform, string enrollmentToken, string baseUrl, TimeSpan lifetime)
    {
        var expiresAtUtc = timeProvider.GetUtcNow().Add(lifetime);
        RemoveExpired();
        for (var attempt = 0; attempt < 32; attempt++)
        {
            var code = RandomNumberGenerator.GetInt32(1_000_000).ToString("D6", CultureInfo.InvariantCulture);
            if (entries.TryAdd(code, new InstallCodeEntry(platform, enrollmentToken, baseUrl, expiresAtUtc))) return code;
        }

        throw new InvalidOperationException("Could not allocate a unique temporary install code.");
    }

    public bool TryConsume(string? code, out InstallCodeEntry entry)
    {
        entry = default!;
        if (code is null || code.Length != 6 || code.Any(character => character is < '0' or > '9') ||
            !entries.TryRemove(code, out var candidate) || candidate.ExpiresAtUtc <= timeProvider.GetUtcNow()) return false;
        entry = candidate;
        return true;
    }

    private void RemoveExpired()
    {
        var nowUtc = timeProvider.GetUtcNow();
        foreach (var pair in entries)
            if (pair.Value.ExpiresAtUtc <= nowUtc) entries.TryRemove(pair.Key, out _);
    }
}

internal sealed record InstallCodeEntry(string Platform, string EnrollmentToken, string BaseUrl, DateTimeOffset ExpiresAtUtc);
