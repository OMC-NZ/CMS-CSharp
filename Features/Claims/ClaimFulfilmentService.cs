using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Features.Claims;

internal sealed partial class ClaimFulfilmentService(IConfiguration configuration)
{
    public async Task<IReadOnlyList<ClaimFulfilmentResult>> FindAsync(
        string claimId,
        CancellationToken cancellationToken) =>
        await FindManyAsync([claimId], cancellationToken);

    public async Task<IReadOnlyList<ClaimFulfilmentResult>> FindManyAsync(
        IReadOnlyList<string> claimIds,
        CancellationToken cancellationToken)
    {
        var normalizedClaimIds = claimIds
            .Select(claimId => claimId.Trim())
            .Where(claimId => claimId.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedClaimIds.Length == 0)
        {
            throw new ClaimValidationException("At least one claimIds query parameter is required.");
        }
        if (normalizedClaimIds.Length > 50)
        {
            throw new ClaimValidationException(
                "A maximum of 50 Claim IDs can be requested at once.");
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
        var idParameters = normalizedClaimIds
            .Select((_, index) => $"@claimId{index}")
            .ToArray();
        await using var command = new MySqlCommand(
            $"""
            SELECT
                c.id,
                g.alias AS sku,
                CONCAT_WS(' ',
                    NULLIF(TRIM(ct.first_name), ''),
                    NULLIF(TRIM(ct.last_name), '')
                ) AS consignee_name,
                ha.street,
                ha.suburb,
                ha.city,
                ha.postcode,
                ct.contact,
                ct.email,
                ha.instructions
            FROM Claims c
            INNER JOIN Customers ct ON ct.id = c.customer_id
            INNER JOIN Claim_Gifts cg ON cg.claim_id = c.id
            INNER JOIN Gifts g ON g.id = cg.gift_id
            LEFT JOIN Deliver_Addresses ha ON ha.id = (
                SELECT ha2.id
                FROM Deliver_Addresses ha2
                WHERE ha2.claim_id = c.id
                ORDER BY ha2.is_current DESC, ha2.id DESC
                LIMIT 1
            )
            WHERE c.id IN ({string.Join(", ", idParameters)})
            ORDER BY FIELD(c.id, {string.Join(", ", idParameters)}), cg.id;
            """,
            connection);
        for (var index = 0; index < normalizedClaimIds.Length; index++)
        {
            command.Parameters.AddWithValue(idParameters[index], normalizedClaimIds[index]);
        }

        var results = new List<ClaimFulfilmentResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ClaimFulfilmentResult(
                reader.GetString("id"),
                string.Empty,
                string.Empty,
                "GWP",
                reader.GetString("sku"),
                1,
                reader.GetString("consignee_name"),
                GetNullableString(reader, "street") ?? string.Empty,
                GetNullableString(reader, "suburb") ?? string.Empty,
                string.Empty,
                GetNullableString(reader, "city") ?? string.Empty,
                GetNullableString(reader, "postcode") ?? string.Empty,
                reader.GetString("contact"),
                reader.GetString("email"),
                GetNullableString(reader, "instructions") ?? string.Empty));
        }

        return results;
    }

    private static string? GetNullableString(MySqlDataReader reader, string columnName)
    {
        var ordinal = reader.GetOrdinal(columnName);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

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

    [GeneratedRegex(@"(?i)Server=([^;,]+),(\d+)")]
    private static partial Regex ConnectionPortRegex();
}

internal sealed record ClaimFulfilmentResult(
    [property: JsonPropertyName("claimId")] string Id,
    [property: JsonPropertyName("clientOrderNumber")] string ClientOrderNumber,
    [property: JsonPropertyName("consigneePoNumber")] string ConsigneePoNumber,
    [property: JsonPropertyName("lineId")] string LineId,
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("qty")] int Quantity,
    [property: JsonPropertyName("fullName")] string ConsigneeName,
    [property: JsonPropertyName("street")] string Street,
    [property: JsonPropertyName("suburb")] string Suburb,
    [property: JsonPropertyName("shippingAddressLine3")] string ShippingAddressLine3,
    [property: JsonPropertyName("city")] string City,
    [property: JsonPropertyName("postcode")] string Postcode,
    [property: JsonPropertyName("contact")] string Contact,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("instructions")] string Instructions);
