namespace HyPanel.Server.Persistence;

using System.Globalization;

internal static class SqliteValue
{
    public static string ToUtcText(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    public static DateTimeOffset ToDateTimeOffset(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    public static Guid ToGuid(string value) => Guid.ParseExact(value, "D");

    public static object ToDbValue(DateTimeOffset? value) => value is null ? DBNull.Value : ToUtcText(value.Value);

    public static object ToDbValue(string? value) => value ?? (object)DBNull.Value;
}
