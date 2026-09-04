using System.Text.RegularExpressions;
using CMS_CSharp.Services.Storage;
using MySqlConnector;

namespace CMS_CSharp.Features.Claims;

internal sealed partial class ClaimDetailsService(
    IConfiguration configuration,
    IR2StorageService r2Storage)
{
    private const string ClaimAssetsPrefix = "claims/promotions";

    public async Task<ClaimDetailsResult?> FindAsync(
        string claimId,
        CancellationToken cancellationToken)
    {
        var normalizedClaimId = claimId.Trim();
        if (normalizedClaimId.Length == 0)
        {
            throw new ClaimValidationException("claimId is required.");
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
            """
            SELECT
                c.id AS claim_id,
                c.promotion_id,
                p.name AS promotion_name,
                c.status,
                ct.contact,
                CONCAT_WS(', ',
                    NULLIF(TRIM(ha.street), ''),
                    NULLIF(TRIM(ha.suburb), ''),
                    NULLIF(TRIM(ha.city), ''),
                    NULLIF(TRIM(ha.postcode), '')
                ) AS full_address,
                c.receipt_url,
                c.screenshot_url,
                CASE WHEN c.status IN (1, 2) THEN (
                    SELECT d.reference
                    FROM Deliveries d
                    WHERE d.claim_id = c.id
                    ORDER BY d.id DESC
                    LIMIT 1
                ) END AS delivery_reference,
                CASE WHEN c.status = 2 THEN (
                    SELECT tt.track_link
                    FROM Track_Trace tt
                    WHERE tt.address_id = ha.id
                    ORDER BY tt.created_at DESC, tt.id DESC
                    LIMIT 1
                ) END AS track_link
            FROM Claims c
            INNER JOIN Customers ct ON ct.id = c.customer_id
            INNER JOIN Promotions p ON p.id = c.promotion_id
            LEFT JOIN Deliver_Addresses ha ON ha.id = (
                SELECT ha2.id
                FROM Deliver_Addresses ha2
                WHERE ha2.claim_id = c.id
                  AND ha2.is_current = 1
                ORDER BY ha2.id DESC
                LIMIT 1
            )
            WHERE c.id = @claimId
            LIMIT 1;
            """,
            connection);
        command.Parameters.AddWithValue("@claimId", normalizedClaimId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var promotionId = reader.GetInt32("promotion_id");
        var receiptValue = NormalizeStoredFileValue(reader.GetString("receipt_url"));
        var screenshotValue = NormalizeStoredFileValue(reader.GetString("screenshot_url"));
        var claimAssetsPrefix = $"{ClaimAssetsPrefix}/{promotionId}";
        var receiptUrlTask = r2Storage.ResolvePublicUrlByPrefixAsync(
            receiptValue, claimAssetsPrefix, cancellationToken);
        var screenshotUrlTask = r2Storage.ResolvePublicUrlByPrefixAsync(
            screenshotValue, claimAssetsPrefix, cancellationToken);
        await Task.WhenAll(receiptUrlTask, screenshotUrlTask);

        return new ClaimDetailsResult(
            reader.GetString("claim_id"),
            reader.GetString("promotion_name"),
            reader.GetString("contact"),
            GetNullableString(reader, "full_address") ?? string.Empty,
            await receiptUrlTask,
            await screenshotUrlTask,
            GetNullableString(reader, "delivery_reference"),
            GetNullableString(reader, "track_link"));
    }

    private static string? GetNullableString(MySqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static string NormalizeStoredFileValue(string storedValue) =>
        Uri.TryCreate(storedValue, UriKind.Absolute, out _)
            ? storedValue
            : Path.GetFileName(storedValue.Replace('\\', '/'));

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
}

internal sealed record ClaimDetailsResult(
    string ClaimId,
    string PromotionName,
    string Contact,
    string FullAddress,
    string ReceiptUrl,
    string ScreenshotUrl,
    string? Reference,
    string? TrackLink);
