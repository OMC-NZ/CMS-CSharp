using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Features.Promotions;

internal sealed partial class EligiblePromotionLookupService(IConfiguration configuration)
{
    public async Task<EligiblePromotionsResult?> FindByImeiAsync(
        string imei,
        CancellationToken cancellationToken)
    {
        var normalizedImei = imei.Trim();
        if (normalizedImei.Length != 15 ||
            normalizedImei.Any(character => !char.IsAsciiDigit(character)))
        {
            throw new PromotionValidationException("imei must contain exactly 15 digits.");
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured.");
        }

        var publicAssetsUrl = configuration["R2_PUBLIC_ASSETS_URL"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(publicAssetsUrl) ||
            !Uri.TryCreate(publicAssetsUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "R2_PUBLIC_ASSETS_URL is not configured with a valid absolute URL.");
        }

        await using var connection = new MySqlConnection(
            NormalizeMySqlConnectionString(connectionString));
        await connection.OpenAsync(cancellationToken);

        await using var command = new MySqlCommand(
            """
            SELECT
                d.imei,
                p.id,
                p.name,
                p.banner_url,
                c.name AS channel_name,
                MAX(pc.start_date) AS latest_start_date,
                MAX(pc.end_date) AS latest_end_date,
                MAX(pc.redeem_end_date) AS latest_redeem_end_date
            FROM Devices d
            LEFT JOIN Promotion_Devices pd
                ON pd.eligible_model = d.model
            LEFT JOIN Promotions p
                ON p.id = pd.promotion_id
            LEFT JOIN Promotion_Channels pc
                ON pc.promotion_id = p.id AND pc.channel_code = d.channel_code
            LEFT JOIN Channels c
                ON c.code = pc.channel_code
            WHERE d.imei = @imei
            GROUP BY d.imei, p.id, p.name, p.banner_url, c.name
            ORDER BY latest_start_date DESC, latest_end_date DESC, p.id DESC
            LIMIT 2;

            SELECT id, status
            FROM Claims
            WHERE imei = @imei
            ORDER BY created_at DESC, id DESC;
            """,
            connection);
        command.Parameters.AddWithValue("@imei", normalizedImei);

        var promotions = new List<EligiblePromotionResult>();
        var claimIds = new List<EligiblePromotionClaimResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        do
        {
            if (!reader.IsDBNull(1) && !reader.IsDBNull(4))
            {
                promotions.Add(new EligiblePromotionResult(
                    reader.GetInt32(1),
                    reader.GetString(2),
                    BuildBannerUrl(publicAssetsUrl, reader.GetString(3)),
                    reader.GetString(4),
                    reader.GetDateTime(5).ToString("yyyy-MM-dd HH:mm:ss"),
                    reader.GetDateTime(6).ToString("yyyy-MM-dd HH:mm:ss"),
                    reader.GetDateTime(7).ToString("yyyy-MM-dd HH:mm:ss")));
            }
        }
        while (await reader.ReadAsync(cancellationToken));

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                claimIds.Add(new EligiblePromotionClaimResult(
                    reader.GetString(0),
                    Convert.ToInt32(reader.GetValue(1))));
            }
        }

        return new EligiblePromotionsResult(
            normalizedImei,
            claimIds,
            promotions);
    }

    private static string BuildBannerUrl(string publicAssetsUrl, string bannerValue)
    {
        if (Uri.TryCreate(bannerValue, UriKind.Absolute, out var existingUrl))
        {
            return existingUrl.ToString();
        }

        var fileName = Path.GetFileName(bannerValue.Replace('\\', '/'));
        return $"{publicAssetsUrl}/banners/Promotions/{Uri.EscapeDataString(fileName)}";
    }

    private static string NormalizeMySqlConnectionString(string connectionString)
    {
        var normalized = Regex.Replace(
            connectionString,
            @"(?i)Server=([^;,]+),(\d+)",
            "Server=$1;Port=$2");
        normalized = normalized.Replace(
            "Encrypt=True;", "SslMode=Preferred;", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "Encrypt=False;", "SslMode=None;", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "TrustServerCertificate=True;", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "TrustServerCertificate=False;", string.Empty, StringComparison.OrdinalIgnoreCase);
        return normalized;
    }

}

internal sealed record EligiblePromotionsResult(
    string Imei,
    IReadOnlyList<EligiblePromotionClaimResult> ClaimIds,
    IReadOnlyList<EligiblePromotionResult> Promotions);

internal sealed record EligiblePromotionClaimResult(
    string Id,
    int Status);

internal sealed record EligiblePromotionResult(
    int Id,
    string Name,
    string BannerUrl,
    string ChannelName,
    string StartDate,
    string EndDate,
    string RedeemEndDate);
