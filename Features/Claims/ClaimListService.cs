using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Features.Claims;

internal sealed partial class ClaimListService(IConfiguration configuration)
{
    public async Task<IReadOnlyList<ClaimListResult>> GetAllAsync(
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

        var rangeEndUtc = DateTime.UtcNow;
        var newZealandTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland");
        var nowInNewZealand = TimeZoneInfo.ConvertTimeFromUtc(rangeEndUtc, newZealandTimeZone);
        var daysSinceMonday = ((int)nowInNewZealand.DayOfWeek + 6) % 7;
        var previousMondayInNewZealand = nowInNewZealand.Date
            .AddDays(-daysSinceMonday - 7);
        var rangeStartUtc = TimeZoneInfo.ConvertTimeToUtc(
            DateTime.SpecifyKind(previousMondayInNewZealand, DateTimeKind.Unspecified),
            newZealandTimeZone);

        await using var command = new MySqlCommand(
            """
            SELECT
                c.id AS claim_id,
                c.imei,
                CONCAT_WS(' ', NULLIF(TRIM(ct.first_name), ''), NULLIF(TRIM(ct.last_name), '')) AS full_name,
                ct.email,
                c.status,
                CASE
                    WHEN g.id IS NULL THEN NULL
                    WHEN NULLIF(TRIM(g.color), '') IS NULL
                         OR LOWER(TRIM(g.color)) = 'empty' THEN TRIM(g.name)
                    ELSE CONCAT(TRIM(g.name), ' ', TRIM(g.color))
                END AS gift_name,
                c.created_at
            FROM Claims c
            INNER JOIN Customers ct ON ct.id = c.customer_id
            LEFT JOIN Claim_Gifts cg ON cg.claim_id = c.id
            LEFT JOIN Gifts g ON g.id = cg.gift_id
            WHERE c.created_at >= @rangeStartUtc
              AND c.created_at <= @rangeEndUtc
            ORDER BY c.created_at DESC, c.id DESC, g.name;
            """,
            connection);
        command.Parameters.AddWithValue("@rangeStartUtc", rangeStartUtc);
        command.Parameters.AddWithValue("@rangeEndUtc", rangeEndUtc);

        return await ReadResultsAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<ClaimListResult>> SearchByClaimIdAsync(
        string claimId,
        CancellationToken cancellationToken) =>
        await SearchAsync(claimId, "claim_id", "c.id", cancellationToken);

    public async Task<IReadOnlyList<ClaimListResult>> SearchByImeiAsync(
        string imei,
        CancellationToken cancellationToken) =>
        await SearchAsync(imei, "imei", "c.imei", cancellationToken);

    public async Task<IReadOnlyList<ClaimListResult>> SearchByEmailAsync(
        string email,
        CancellationToken cancellationToken) =>
        await SearchAsync(email, "email", "ct.email", cancellationToken);

    private async Task<IReadOnlyList<ClaimListResult>> SearchAsync(
        string value,
        string fieldName,
        string trustedColumn,
        CancellationToken cancellationToken)
    {
        var searchValue = value.Trim();
        if (searchValue.Length == 0)
        {
            throw new ClaimValidationException($"{fieldName} is required.");
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured.");
        }

        await using var connection = new MySqlConnection(
            NormalizeMySqlConnectionString(connectionString));
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(
            $"""
            SELECT
                c.id AS claim_id,
                c.imei,
                CONCAT_WS(' ', NULLIF(TRIM(ct.first_name), ''), NULLIF(TRIM(ct.last_name), '')) AS full_name,
                ct.email,
                c.status,
                CASE
                    WHEN g.id IS NULL THEN NULL
                    WHEN NULLIF(TRIM(g.color), '') IS NULL
                         OR LOWER(TRIM(g.color)) = 'empty' THEN TRIM(g.name)
                    ELSE CONCAT(TRIM(g.name), ' ', TRIM(g.color))
                END AS gift_name,
                c.created_at
            FROM Claims c
            INNER JOIN Customers ct ON ct.id = c.customer_id
            LEFT JOIN Claim_Gifts cg ON cg.claim_id = c.id
            LEFT JOIN Gifts g ON g.id = cg.gift_id
            WHERE {trustedColumn} LIKE CONCAT('%', @searchValue, '%') ESCAPE '='
            ORDER BY c.created_at DESC, c.id DESC, g.name;
            """,
            connection);
        command.Parameters.AddWithValue("@searchValue", EscapeLikePattern(searchValue));

        return await ReadResultsAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<ClaimListResult>> ReadResultsAsync(
        MySqlCommand command,
        CancellationToken cancellationToken)
    {
        var claims = new Dictionary<string, ClaimListBuilder>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var claimId = reader.GetString("claim_id");
            if (!claims.TryGetValue(claimId, out var claim))
            {
                claim = new ClaimListBuilder(
                    claimId,
                    reader.GetString("imei"),
                    reader.GetString("full_name"),
                    reader.GetString("email"),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("status"))),
                    reader.GetDateTime("created_at"));
                claims.Add(claimId, claim);
            }

            if (!reader.IsDBNull(reader.GetOrdinal("gift_name")))
            {
                claim.Gifts.Add(reader.GetString("gift_name"));
            }
        }

        return claims.Values
            .Select(claim => new ClaimListResult(
                claim.ClaimId,
                claim.Imei,
                claim.FullName,
                claim.Email,
                claim.Status,
                claim.Gifts.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                claim.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss")))
            .ToArray();
    }

    private static string EscapeLikePattern(string value) => value
        .Replace("=", "==", StringComparison.Ordinal)
        .Replace("%", "=%", StringComparison.Ordinal)
        .Replace("_", "=_", StringComparison.Ordinal);

    private static string NormalizeMySqlConnectionString(string connectionString)
    {
        var normalized = ConnectionPortRegex().Replace(connectionString, "Server=$1;Port=$2");
        normalized = normalized.Replace(
            "Encrypt=True;", "SslMode=Preferred;", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "Encrypt=False;", "SslMode=None;", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "TrustServerCertificate=True;", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "TrustServerCertificate=False;", string.Empty, StringComparison.OrdinalIgnoreCase);
        var builder = new MySqlConnectionStringBuilder(normalized)
        {
            TreatTinyAsBoolean = false
        };
        return builder.ConnectionString;
    }

    [GeneratedRegex(@"(?i)Server=([^;,]+),(\d+)")]
    private static partial Regex ConnectionPortRegex();

    private sealed record ClaimListBuilder(
        string ClaimId,
        string Imei,
        string FullName,
        string Email,
        int Status,
        DateTime CreatedAt)
    {
        public HashSet<string> Gifts { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed record ClaimListResult(
    string ClaimId,
    string Imei,
    string FullName,
    string Email,
    int Status,
    IReadOnlyList<string> Gifts,
    string CreatedAt);
