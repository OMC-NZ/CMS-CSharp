using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Features.Promotions;

internal sealed partial class PromotionDetailsService(IConfiguration configuration)
{
    public async Task<PromotionDetailsResult?> FindAsync(
        int promotionId,
        CancellationToken cancellationToken)
    {
        if (promotionId < 0)
        {
            throw new PromotionValidationException(
                "promotionId must be zero or a positive integer.");
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
            SELECT description, slug_url, terms_url
            FROM Promotions
            WHERE id = @promotionId
            LIMIT 1;

            SELECT c.name, pc.start_date, pc.end_date
            FROM Promotion_Channels pc
            INNER JOIN Channels c ON c.code = pc.channel_code
            WHERE pc.promotion_id = @promotionId
            ORDER BY pc.start_date, pc.end_date, c.name;

            SELECT DISTINCT d.market_name, pd.eligible_model
            FROM Promotion_Devices pd
            INNER JOIN Devices d ON d.model = pd.eligible_model
            WHERE pd.promotion_id = @promotionId
              AND NULLIF(TRIM(d.market_name), '') IS NOT NULL
              AND LOWER(TRIM(d.market_name)) <> 'temp'
            ORDER BY d.market_name, pd.eligible_model;
            """,
            connection);
        command.Parameters.AddWithValue("@promotionId", promotionId);

        string description;
        string slugUrl;
        string termsUrl;
        var channels = new List<PromotionDetailsChannel>();
        var devices = new List<PromotionDetailsDevice>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        description = reader.GetString(0);
        slugUrl = reader.GetString(1);
        termsUrl = reader.GetString(2);

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                channels.Add(new PromotionDetailsChannel(
                    reader.GetString(0),
                    reader.GetDateTime(1).ToString("yyyy-MM-dd HH:mm:ss"),
                    reader.GetDateTime(2).ToString("yyyy-MM-dd HH:mm:ss")));
            }
        }

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                devices.Add(new PromotionDetailsDevice(
                    reader.GetString(0),
                    reader.GetString(1)));
            }
        }

        return new PromotionDetailsResult(
            promotionId,
            description,
            slugUrl,
            termsUrl,
            channels,
            devices);
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

internal sealed record PromotionDetailsResult(
    int PromotionId,
    string Description,
    string SlugUrl,
    string TermsUrl,
    IReadOnlyList<PromotionDetailsChannel> Channels,
    IReadOnlyList<PromotionDetailsDevice> Devices);

internal sealed record PromotionDetailsChannel(
    string Name,
    string StartDate,
    string EndDate);

internal sealed record PromotionDetailsDevice(
    string MarketName,
    string Model);
