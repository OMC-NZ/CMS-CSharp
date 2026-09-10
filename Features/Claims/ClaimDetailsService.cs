using System.Text.RegularExpressions;
using CMS_CSharp.Services.Storage;
using MySqlConnector;

namespace CMS_CSharp.Features.Claims;

internal sealed partial class ClaimDetailsService(
    IConfiguration configuration,
    IR2StorageService r2Storage)
{
    private const string ClaimAssetsPrefix = "claims/promotions";

    public async Task<ClaimDetailsResult?> FindAsync(string claimId, CancellationToken cancellationToken)
    {
        var normalizedClaimId = claimId.Trim();
        if (normalizedClaimId.Length == 0)
        {
            throw new ClaimValidationException("claimId is required.");
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
        }

        await using var connection = new MySqlConnection(NormalizeMySqlConnectionString(connectionString));
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            SELECT
                c.promotion_id,
                p.name AS promotion_name,
                c.status,
                ct.email,
                ct.contact,
                ha.street,
                ha.suburb,
                ha.city,
                ha.postcode,
                ha.instructions,
                CONCAT_WS(', ', NULLIF(TRIM(ha.street), ''), NULLIF(TRIM(ha.suburb), ''),
                    NULLIF(TRIM(ha.city), ''), NULLIF(TRIM(ha.postcode), '')) AS full_address,
                c.receipt_url,
                c.screenshot_url,
                CASE WHEN c.status IN (1, 2) THEN (
                    SELECT d.reference FROM Deliveries d
                    WHERE d.claim_id = c.id ORDER BY d.id DESC LIMIT 1
                ) END AS delivery_reference,
                CASE WHEN c.status = 2 THEN (
                    SELECT tt.track_link FROM Track_Trace tt
                    WHERE tt.address_id = ha.id ORDER BY tt.created_at DESC, tt.id DESC LIMIT 1
                ) END AS track_link
            FROM Claims c
            INNER JOIN Customers ct ON ct.id = c.customer_id
            INNER JOIN Promotions p ON p.id = c.promotion_id
            LEFT JOIN Deliver_Addresses ha ON ha.id = (
                SELECT ha2.id FROM Deliver_Addresses ha2
                WHERE ha2.claim_id = c.id AND ha2.is_current = 1
                ORDER BY ha2.id DESC LIMIT 1
            )
            WHERE c.id = @claimId
            LIMIT 1;
            """,
            connection);
        command.Parameters.AddWithValue("@claimId", normalizedClaimId);

        ClaimRow row;
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            row = new ClaimRow(
                reader.GetInt32("promotion_id"),
                reader.GetString("promotion_name"),
                reader.GetString("email"),
                reader.GetString("contact"),
                GetNullableString(reader, "street") ?? string.Empty,
                GetNullableString(reader, "suburb") ?? string.Empty,
                GetNullableString(reader, "city") ?? string.Empty,
                GetNullableString(reader, "postcode") ?? string.Empty,
                GetNullableString(reader, "instructions") ?? string.Empty,
                GetNullableString(reader, "full_address") ?? string.Empty,
                NormalizeStoredFileValue(reader.GetString("receipt_url")),
                NormalizeStoredFileValue(reader.GetString("screenshot_url")),
                GetNullableString(reader, "delivery_reference"),
                GetNullableString(reader, "track_link"));
        }

        var giftAliases = await LoadGiftAliasesAsync(connection, normalizedClaimId, cancellationToken);
        var prefix = $"{ClaimAssetsPrefix}/{row.PromotionId}";
        var receiptTask = r2Storage.ResolvePublicAssetByPrefixAsync(row.ReceiptValue, prefix, cancellationToken);
        var imeiCopyTask = r2Storage.ResolvePublicAssetByPrefixAsync(row.ImeiCopyValue, prefix, cancellationToken);
        await Task.WhenAll(receiptTask, imeiCopyTask);
        var receipt = await receiptTask;
        var imeiCopy = await imeiCopyTask;

        return new ClaimDetailsResult(
            normalizedClaimId, row.PromotionName, row.Email, row.Contact,
            row.Street, row.Suburb, row.City, row.Postcode, row.Instructions, row.FullAddress,
            giftAliases, receipt.PublicUrl, receipt.Sha256, imeiCopy.PublicUrl, imeiCopy.Sha256,
            row.Reference, row.TrackLink);
    }

    private static async Task<IReadOnlyList<string>> LoadGiftAliasesAsync(
        MySqlConnection connection, string claimId, CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT g.alias FROM Claim_Gifts cg
            INNER JOIN Gifts g ON g.id = cg.gift_id
            WHERE cg.claim_id = @claimId ORDER BY cg.id;
            """,
            connection);
        command.Parameters.AddWithValue("@claimId", claimId);
        var aliases = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            aliases.Add(reader.GetString(0));
        }
        return aliases;
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
        var normalized = ConnectionPortRegex().Replace(connectionString, "Server=$1;Port=$2")
            .Replace("Encrypt=True;", "SslMode=Preferred;", StringComparison.OrdinalIgnoreCase)
            .Replace("Encrypt=False;", "SslMode=None;", StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=True;", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=False;", string.Empty, StringComparison.OrdinalIgnoreCase);
        return new MySqlConnectionStringBuilder(normalized) { TreatTinyAsBoolean = false }.ConnectionString;
    }

    [GeneratedRegex(@"(?i)Server=([^;,]+),(\d+)")]
    private static partial Regex ConnectionPortRegex();

    private sealed record ClaimRow(
        int PromotionId, string PromotionName, string Email, string Contact,
        string Street, string Suburb, string City, string Postcode, string Instructions,
        string FullAddress, string ReceiptValue, string ImeiCopyValue,
        string? Reference, string? TrackLink);
}

internal sealed record ClaimDetailsResult(
    string ClaimId,
    string PromotionName,
    string Email,
    string Contact,
    string Street,
    string Suburb,
    string City,
    string Postcode,
    string Instructions,
    string FullAddress,
    IReadOnlyList<string> GiftAliases,
    string ReceiptUrl,
    string? ReceiptSha256,
    string ImeiCopyUrl,
    string? ImeiCopySha256,
    string? Reference,
    string? TrackLink);
