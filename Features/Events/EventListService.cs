using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Features.Events;

internal sealed class EventListService(IConfiguration configuration)
{
    public async Task<IReadOnlyList<EventListItem>> GetAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");

        var normalized = Regex.Replace(connectionString, @"(?i)Server=([^;,]+),(\d+)", "Server=$1;Port=$2")
            .Replace("Encrypt=True;", "SslMode=Preferred;", StringComparison.OrdinalIgnoreCase)
            .Replace("Encrypt=False;", "SslMode=None;", StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=True;", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=False;", string.Empty, StringComparison.OrdinalIgnoreCase);
        await using var connection = new MySqlConnection(new MySqlConnectionStringBuilder(normalized)
        {
            TreatTinyAsBoolean = false
        }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand("""
            SELECT id, name, banner_url, start_date, end_date, updated_at
            FROM Events
            ORDER BY id DESC;
            """, connection);

        var now = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(DateTimeOffset.UtcNow, "Pacific/Auckland").DateTime;
        var results = new List<EventListItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var start = reader.IsDBNull(3) ? (DateTime?)null : reader.GetDateTime(3);
            var end = reader.IsDBNull(4) ? (DateTime?)null : reader.GetDateTime(4);
            var updated = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
            var status = start is null || end is null ? null
                : now < start ? "upcoming" : now > end ? "ended" : "active";
            results.Add(new EventListItem(
                reader.GetInt32(0), reader.GetString(1),
                BuildBannerUrl(reader.IsDBNull(2) ? null : reader.GetString(2)),
                start?.ToString("yyyy-MM-dd HH:mm:ss"),
                end?.ToString("yyyy-MM-dd HH:mm:ss"), status,
                updated?.ToString("yyyy-MM-dd HH:mm:ss")));
        }
        return results;
    }

    private string? BuildBannerUrl(string? storedValue)
    {
        if (string.IsNullOrWhiteSpace(storedValue)) return null;
        if (Uri.TryCreate(storedValue, UriKind.Absolute, out var url) &&
            url.Scheme is "http" or "https") return url.AbsoluteUri;

        var baseUrl = configuration["R2_PUBLIC_ASSETS_URL"]?.TrimEnd('/');
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var publicUrl) ||
            publicUrl.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("R2_PUBLIC_ASSETS_URL must be a valid HTTP(S) URL.");

        const string eventPrefix = "banners/Events/imgs/";
        var fileName = Path.GetFileName(storedValue.Trim().Replace('\\', '/'));
        return baseUrl + "/" + eventPrefix + Uri.EscapeDataString(fileName);
    }
}

internal sealed record EventListItem(
    int Id, string Name, string? BannerUrl, string? StartDate,
    string? EndDate, string? Status, string? UpdatedAt);
