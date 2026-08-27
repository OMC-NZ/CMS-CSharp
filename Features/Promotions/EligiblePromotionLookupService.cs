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

        string model;
        string channelCode;
        await using (var deviceCommand = new MySqlCommand(
                         """
                         SELECT model, channel_code
                         FROM Devices
                         WHERE imei = @imei
                         LIMIT 1;
                         """,
                         connection))
        {
            deviceCommand.Parameters.AddWithValue("@imei", normalizedImei);
            await using var reader = await deviceCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            model = reader.GetString(0);
            channelCode = reader.GetString(1);
        }

        await using var promotionCommand = new MySqlCommand(
            """
            SELECT
                p.id,
                p.name,
                p.banner_url,
                MAX(pc.start_date) AS latest_start_date,
                MAX(pc.end_date) AS latest_end_date
            FROM Promotions p
            INNER JOIN Promotion_Devices pd
                ON pd.promotion_id = p.id AND pd.eligible_model = @model
            INNER JOIN Promotion_Channels pc
                ON pc.promotion_id = p.id AND pc.channel_code = @channelCode
            GROUP BY p.id, p.name, p.banner_url
            ORDER BY latest_start_date DESC, latest_end_date DESC, p.id DESC
            LIMIT 2;
            """,
            connection);
        promotionCommand.Parameters.AddWithValue("@model", model);
        promotionCommand.Parameters.AddWithValue("@channelCode", channelCode);

        var promotions = new List<EligiblePromotionResult>();
        await using var promotionReader = await promotionCommand.ExecuteReaderAsync(cancellationToken);
        while (await promotionReader.ReadAsync(cancellationToken))
        {
            promotions.Add(new EligiblePromotionResult(
                promotionReader.GetInt32(0),
                promotionReader.GetString(1),
                BuildBannerUrl(publicAssetsUrl, promotionReader.GetString(2))));
        }

        return new EligiblePromotionsResult(normalizedImei, promotions);
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
    IReadOnlyList<EligiblePromotionResult> Promotions);

internal sealed record EligiblePromotionResult(
    int Id,
    string Name,
    string BannerUrl);
