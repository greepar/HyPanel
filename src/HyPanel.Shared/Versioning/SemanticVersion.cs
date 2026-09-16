namespace HyPanel.Shared.Versioning;

using System.Globalization;

public readonly record struct SemanticVersion(int Major, int Minor, int Patch, string? PreRelease)
    : IComparable<SemanticVersion>
{
    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        var metadataIndex = value.IndexOf('+');
        var comparable = metadataIndex < 0 ? value : value[..metadataIndex];
        var prereleaseIndex = comparable.IndexOf('-');
        var core = prereleaseIndex < 0 ? comparable : comparable[..prereleaseIndex];
        var prerelease = prereleaseIndex < 0 ? null : comparable[(prereleaseIndex + 1)..];
        var parts = core.Split('.');
        if (parts.Length != 3 || !TryNumber(parts[0], out var major) || !TryNumber(parts[1], out var minor)
            || !TryNumber(parts[2], out var patch) || prerelease is not null && !IsValidIdentifiers(prerelease)) return false;
        if (metadataIndex >= 0 && !IsValidIdentifiers(value[(metadataIndex + 1)..])) return false;
        version = new SemanticVersion(major, minor, patch, prerelease);
        return true;
    }

    public static SemanticVersion Parse(string value) =>
        TryParse(value, out var version) ? version : throw new FormatException("Invalid semantic version.");

    public int CompareTo(SemanticVersion other)
    {
        var core = Major.CompareTo(other.Major);
        if (core == 0) core = Minor.CompareTo(other.Minor);
        if (core == 0) core = Patch.CompareTo(other.Patch);
        if (core != 0) return core;
        if (PreRelease is null) return other.PreRelease is null ? 0 : 1;
        if (other.PreRelease is null) return -1;
        var left = PreRelease.Split('.');
        var right = other.PreRelease.Split('.');
        for (var index = 0; index < Math.Min(left.Length, right.Length); index++)
        {
            var leftNumeric = int.TryParse(left[index], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = int.TryParse(right[index], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            var result = leftNumeric && rightNumeric ? leftNumber.CompareTo(rightNumber)
                : leftNumeric ? -1 : rightNumeric ? 1 : string.CompareOrdinal(left[index], right[index]);
            if (result != 0) return result;
        }
        return left.Length.CompareTo(right.Length);
    }

    private static bool TryNumber(string value, out int number)
    {
        number = 0;
        return value.Length > 0 && (value.Length == 1 || value[0] != '0')
               && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number);
    }

    private static bool IsValidIdentifiers(string value)
    {
        if (value.Length == 0) return false;
        foreach (var part in value.Split('.'))
        {
            if (part.Length == 0) return false;
            foreach (var character in part)
                if (!char.IsAsciiLetterOrDigit(character) && character != '-') return false;
        }
        return true;
    }
}
