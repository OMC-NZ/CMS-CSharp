using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Features.Auth;

internal sealed partial class RoleLookupService(IConfiguration configuration)
{
    public async Task<IReadOnlyList<RoleLookupResult>> GetActiveAsync(
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("AuthConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:AuthConnection is not configured.");
        }

        await using var connection = new MySqlConnection(
            NormalizeMySqlConnectionString(connectionString));
        await connection.OpenAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            SELECT id, code, name, description
            FROM roles
            WHERE status = 1
            ORDER BY name, id;
            """,
            connection);

        var roles = new List<RoleLookupResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var descriptionOrdinal = reader.GetOrdinal("description");
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(new RoleLookupResult(
                reader.GetInt32("id"),
                reader.GetString("code"),
                reader.GetString("name"),
                reader.IsDBNull(descriptionOrdinal)
                    ? null
                    : reader.GetString(descriptionOrdinal)));
        }

        return roles;
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

internal sealed record RoleLookupResult(
    int Id,
    string Code,
    string Name,
    string? Description);
