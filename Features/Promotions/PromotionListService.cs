using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Features.Promotions;

internal sealed partial class PromotionListService(IConfiguration configuration)
{
    public async Task<IReadOnlyList<PromotionListItem>> GetAsync(
        CancellationToken cancellationToken)
    {
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
            SELECT id, name, banner_url
            FROM Promotions
            WHERE id > 0
            ORDER BY id DESC
            LIMIT 15;

            SELECT DISTINCT pc.promotion_id, c.category
            FROM Promotion_Channels pc
            INNER JOIN Channels c ON c.code = pc.channel_code
            INNER JOIN (
                SELECT id FROM Promotions WHERE id > 0 ORDER BY id DESC LIMIT 15
            ) recent ON recent.id = pc.promotion_id
            ORDER BY pc.promotion_id DESC, c.category;

            SELECT DISTINCT pd.promotion_id, d.market_name
            FROM Promotion_Devices pd
            INNER JOIN Devices d ON d.model = pd.eligible_model
            INNER JOIN (
                SELECT id FROM Promotions WHERE id > 0 ORDER BY id DESC LIMIT 15
            ) recent ON recent.id = pd.promotion_id
            WHERE NULLIF(TRIM(d.market_name), '') IS NOT NULL
              AND LOWER(TRIM(d.market_name)) <> 'temp'
            ORDER BY pd.promotion_id DESC, d.market_name;

            SELECT DISTINCT pg.promotion_id, g.id, g.name, g.color, g.alias
            FROM Promotion_Gifts pg
            INNER JOIN Gifts g ON g.id = pg.gift_id
            INNER JOIN (
                SELECT id FROM Promotions WHERE id > 0 ORDER BY id DESC LIMIT 15
            ) recent ON recent.id = pg.promotion_id
            ORDER BY pg.promotion_id DESC, g.id;
            """,
            connection);

        var items = new Dictionary<int, PromotionAccumulator>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetInt32(0);
            items[id] = new PromotionAccumulator(
                id,
                reader.GetString(1),
                BuildBannerUrl(publicAssetsUrl, reader.GetString(2)));
        }

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (items.TryGetValue(reader.GetInt32(0), out var item))
                {
                    item.ChannelCategories.Add(reader.GetString(1));
                }
            }
        }

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (items.TryGetValue(reader.GetInt32(0), out var item))
                {
                    item.MarketNames.Add(reader.GetString(1));
                }
            }
        }

        if (await reader.NextResultAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (items.TryGetValue(reader.GetInt32(0), out var item))
                {
                    var name = reader.GetString(2).Trim();
                    var color = reader.IsDBNull(3) ? string.Empty : reader.GetString(3).Trim();
                    var displayName =
                        color.Length == 0 || color.Equals("Empty", StringComparison.OrdinalIgnoreCase)
                            ? name
                            : $"{name} {color}";
                    item.Gifts.TryAdd(
                        reader.GetString(4),
                        new PromotionGiftListItem(displayName, reader.GetString(4)));
                }
            }
        }

        return items.Values
            .OrderByDescending(item => item.Id)
            .Select(item => new PromotionListItem(
                item.Id,
                item.Name,
                item.BannerUrl,
                item.ChannelCategories.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                item.MarketNames.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                item.Gifts.Values
                    .OrderBy(gift => gift.Name, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(gift => gift.Alias, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .ToArray();
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

    private sealed class PromotionAccumulator(int id, string name, string bannerUrl)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public string BannerUrl { get; } = bannerUrl;
        public HashSet<string> ChannelCategories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> MarketNames { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, PromotionGiftListItem> Gifts { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }
}

internal sealed record PromotionListItem(
    int PromotionId,
    string Name,
    string BannerUrl,
    IReadOnlyList<string> ChannelCategories,
    IReadOnlyList<string> MarketNames,
    IReadOnlyList<PromotionGiftListItem> Gifts);

internal sealed record PromotionGiftListItem(
    string Name,
    string Alias);
