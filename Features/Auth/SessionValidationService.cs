using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Features.Auth;

internal sealed partial class SessionValidationService(IConfiguration configuration)
{
    public async Task<SessionAccessValidationResult?> ValidateAsync(
        int accountId,
        int userId,
        string sessionId,
        int securityVersion,
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
            SELECT
                r.code AS role_code,
                p.code AS permission_code
            FROM sessions AS s
            INNER JOIN accounts AS a ON a.id = s.account_id
            INNER JOIN users AS u ON u.account_id = a.id
            LEFT JOIN account_roles AS ar ON ar.account_id = a.id
            LEFT JOIN roles AS r
                ON r.id = ar.role_id
               AND r.status = 1
            LEFT JOIN role_permissions AS rp ON rp.role_id = r.id
            LEFT JOIN permissions AS p
                ON p.id = rp.permission_id
               AND p.status = 1
            WHERE s.id = @sessionId
              AND s.account_id = @accountId
              AND s.security_version = @securityVersion
              AND s.revoked_at IS NULL
              AND s.expires_at > @nowUtc
              AND a.status = 1
              AND a.security_version = @securityVersion
              AND u.id = @userId
              AND u.status = 1
            """,
            connection);
        command.Parameters.AddWithValue("@sessionId", sessionId);
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.AddWithValue("@securityVersion", securityVersion);
        command.Parameters.AddWithValue("@nowUtc", DateTime.UtcNow);
        command.Parameters.AddWithValue("@userId", userId);
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var found = false;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var roleOrdinal = reader.GetOrdinal("role_code");
        var permissionOrdinal = reader.GetOrdinal("permission_code");
        while (await reader.ReadAsync(cancellationToken))
        {
            found = true;
            if (!reader.IsDBNull(roleOrdinal))
            {
                roles.Add(reader.GetString(roleOrdinal));
            }
            if (!reader.IsDBNull(permissionOrdinal))
            {
                permissions.Add(reader.GetString(permissionOrdinal));
            }
        }

        return found
            ? new SessionAccessValidationResult(
                roles.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                permissions.Order(StringComparer.OrdinalIgnoreCase).ToArray())
            : null;
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

internal sealed record SessionAccessValidationResult(
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);
