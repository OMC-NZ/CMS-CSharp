using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Features.Devices;

internal sealed partial class DeviceLookupService(IConfiguration configuration)
{
    private const int ResultLimit = 30;
    private const int SearchValueLimit = 100;

    public Task<DeviceLookupResult> FindByImeiAsync(
        IReadOnlyList<string>? imeis,
        CancellationToken cancellationToken)
    {
        var normalized = ExpandValues(imeis, "imei", StringComparer.Ordinal);
        var invalid = normalized.FirstOrDefault(value => !ImeiRegex().IsMatch(value));
        if (invalid is not null)
        {
            throw new DeviceLookupValidationException(
                $"imei '{invalid}' must contain exactly 15 digits.");
        }

        return SearchAsync(CreateInPredicate("d.imei", normalized.Count), normalized,
            cancellationToken);
    }

    public Task<DeviceLookupResult> FindByModelAsync(
        IReadOnlyList<string>? models,
        CancellationToken cancellationToken)
    {
        var values = ExpandValues(models, "model", StringComparer.OrdinalIgnoreCase);
        var normalized = values.Select(NormalizeModel)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return SearchAsync(CreateInPredicate("UPPER(d.model)", normalized.Length), normalized,
            cancellationToken);
    }

    public Task<DeviceLookupResult> FindByMarketNameAsync(
        IReadOnlyList<string>? marketNames,
        CancellationToken cancellationToken)
    {
        var normalized = ExpandValues(
                marketNames, "market_name", StringComparer.OrdinalIgnoreCase)
            .Select(EscapeLikePattern)
            .ToArray();

        return SearchAsync(
            string.Join(" OR ", Enumerable.Range(0, normalized.Length)
                .Select(index =>
                    $"d.market_name LIKE CONCAT('%', @value{index}, '%') ESCAPE '='")),
            normalized,
            cancellationToken);
    }

    private async Task<DeviceLookupResult> SearchAsync(
        string trustedPredicate,
        IReadOnlyList<string> values,
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured.");
        }

        await using var connection = new MySqlConnection(
            NormalizeMySqlConnectionString(connectionString));
        await connection.OpenAsync(cancellationToken);

        await using var countCommand = new MySqlCommand(
            $"SELECT COUNT(*) FROM Devices d WHERE {trustedPredicate};",
            connection);
        AddParameters(countCommand, values);
        var total = Convert.ToInt32(
            await countCommand.ExecuteScalarAsync(cancellationToken));

        if (total == 0)
        {
            return new DeviceLookupResult(0, []);
        }

        await using var command = new MySqlCommand(
            $"""
            SELECT
                d.imei,
                d.model,
                d.category,
                d.market_name,
                d.color,
                c.name AS channel_name,
                d.redemption_status,
                d.created_at,
                d.updated_at
            FROM Devices d
            LEFT JOIN Channels c ON c.code = d.channel_code
            WHERE {trustedPredicate}
            ORDER BY d.created_at DESC, d.imei
            LIMIT {ResultLimit};
            """,
            connection);
        AddParameters(command, values);

        var items = new List<DeviceLookupItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new DeviceLookupItem(
                reader.GetString("imei"),
                reader.GetString("model"),
                Convert.ToInt32(reader.GetValue(reader.GetOrdinal("category"))),
                GetNullableString(reader, "market_name"),
                GetNullableString(reader, "color"),
                GetNullableString(reader, "channel_name"),
                Convert.ToInt32(reader.GetValue(reader.GetOrdinal("redemption_status"))),
                GetNullableDateTime(reader, "created_at")?.ToString("yyyy-MM-dd HH:mm:ss"),
                GetNullableDateTime(reader, "updated_at")?.ToString("yyyy-MM-dd HH:mm:ss")));
        }

        return new DeviceLookupResult(total, items);
    }

    private static IReadOnlyList<string> ExpandValues(
        IReadOnlyList<string>? rawValues,
        string fieldName,
        IEqualityComparer<string> comparer)
    {
        var values = (rawValues ?? [])
            .SelectMany(value => value.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Where(value => value.Length > 0)
            .Distinct(comparer)
            .ToArray();

        if (values.Length == 0)
        {
            throw new DeviceLookupValidationException($"{fieldName} is required.");
        }
        if (values.Length > SearchValueLimit)
        {
            throw new DeviceLookupValidationException(
                $"{fieldName} accepts at most {SearchValueLimit} values per request.");
        }

        return values;
    }

    private static string CreateInPredicate(string trustedColumn, int valueCount) =>
        $"{trustedColumn} IN ({string.Join(',', Enumerable.Range(0, valueCount).Select(index => $"@value{index}"))})";

    private static void AddParameters(
        MySqlCommand command,
        IReadOnlyList<string> values)
    {
        for (var index = 0; index < values.Count; index++)
        {
            command.Parameters.AddWithValue($"@value{index}", values[index]);
        }
    }

    private static string NormalizeModel(string value)
    {
        var normalized = value.Trim().ToUpperInvariant();
        if (DigitsOnlyRegex().IsMatch(normalized))
        {
            normalized = $"CPH{normalized}";
        }

        if (!ModelRegex().IsMatch(normalized))
        {
            throw new DeviceLookupValidationException(
                "model must use CPH followed by digits, or contain digits only.");
        }

        return normalized;
    }

    private static string? GetNullableString(MySqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static DateTime? GetNullableDateTime(MySqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("=", "==", StringComparison.Ordinal)
            .Replace("%", "=%", StringComparison.Ordinal)
            .Replace("_", "=_", StringComparison.Ordinal);

    private static string NormalizeMySqlConnectionString(string connectionString)
    {
        var normalized = ConnectionPortRegex().Replace(connectionString, "Server=$1;Port=$2")
            .Replace("Encrypt=True;", "SslMode=Preferred;", StringComparison.OrdinalIgnoreCase)
            .Replace("Encrypt=False;", "SslMode=None;", StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=True;", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=False;", string.Empty, StringComparison.OrdinalIgnoreCase);
        return new MySqlConnectionStringBuilder(normalized)
        {
            TreatTinyAsBoolean = false
        }.ConnectionString;
    }

    [GeneratedRegex(@"^\d{15}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImeiRegex();

    [GeneratedRegex(@"^\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex DigitsOnlyRegex();

    [GeneratedRegex(@"^CPH\d+$", RegexOptions.CultureInvariant)]
    private static partial Regex ModelRegex();

    [GeneratedRegex(@"(?i)Server=([^;,]+),(\d+)")]
    private static partial Regex ConnectionPortRegex();
}

internal sealed record DeviceLookupResult(
    int Total,
    IReadOnlyList<DeviceLookupItem> Items);

internal sealed record DeviceLookupItem(
    string Imei,
    string Model,
    int Category,
    string? MarketName,
    string? Color,
    string? ChannelName,
    int RedemptionStatus,
    string? CreatedAt,
    string? UpdatedAt);

internal sealed class DeviceLookupValidationException(string message)
    : Exception(message);
