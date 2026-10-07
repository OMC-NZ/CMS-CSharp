using System.Text.RegularExpressions;
using CMS_CSharp.Validation;
using MySqlConnector;

namespace CMS_CSharp.Features.Auth;

internal sealed partial class AccountListService(IConfiguration configuration)
{
    public Task<AccountListResult> GetAllAsync(CancellationToken cancellationToken) =>
        QueryAsync([], null, cancellationToken);

    public async Task<AccountDetailsResult?> FindByAccountIdAsync(
        int accountId,
        CancellationToken cancellationToken)
    {
        if (accountId <= 0)
        {
            throw new AccountValidationException(
                "accountId must be a positive integer.");
        }

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
                a.id AS account_id,
                a.email,
                a.status AS account_status,
                a.security_version,
                a.last_login_at,
                u.first_name,
                u.last_name,
                u.display_name,
                u.status AS user_status,
                r.id AS role_id,
                r.name AS role_name,
                p.id AS permission_id,
                p.code AS permission_code,
                p.module AS permission_module,
                p.action AS permission_action
            FROM accounts AS a
            LEFT JOIN users AS u ON u.account_id = a.id
            LEFT JOIN account_roles AS ar ON ar.account_id = a.id
            LEFT JOIN roles AS r ON r.id = ar.role_id
            LEFT JOIN role_permissions AS rp
                ON rp.role_id = r.id
               AND r.status = 1
            LEFT JOIN permissions AS p
                ON p.id = rp.permission_id
               AND p.status = 1
            WHERE a.id = @accountId
            ORDER BY r.name, p.code;
            """,
            connection);
        command.Parameters.AddWithValue("@accountId", accountId);

        AccountDetailsAccumulator? account = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var firstNameOrdinal = reader.GetOrdinal("first_name");
        var lastNameOrdinal = reader.GetOrdinal("last_name");
        var displayNameOrdinal = reader.GetOrdinal("display_name");
        var userStatusOrdinal = reader.GetOrdinal("user_status");
        var lastLoginOrdinal = reader.GetOrdinal("last_login_at");
        var roleIdOrdinal = reader.GetOrdinal("role_id");
        var permissionIdOrdinal = reader.GetOrdinal("permission_id");
        while (await reader.ReadAsync(cancellationToken))
        {
            account ??= new AccountDetailsAccumulator(
                reader.GetInt32("account_id"),
                reader.GetString("email"),
                reader.IsDBNull(firstNameOrdinal) ? null : reader.GetString(firstNameOrdinal),
                reader.IsDBNull(lastNameOrdinal) ? null : reader.GetString(lastNameOrdinal),
                reader.IsDBNull(displayNameOrdinal) ? null : reader.GetString(displayNameOrdinal),
                Convert.ToInt32(reader.GetValue(reader.GetOrdinal("account_status"))),
                reader.IsDBNull(userStatusOrdinal)
                    ? null
                    : Convert.ToInt32(reader.GetValue(userStatusOrdinal)),
                reader.GetInt32("security_version"),
                reader.IsDBNull(lastLoginOrdinal) ? null : reader.GetDateTime(lastLoginOrdinal));

            if (!reader.IsDBNull(roleIdOrdinal))
            {
                account.Roles.TryAdd(
                    reader.GetInt32(roleIdOrdinal),
                    reader.GetString("role_name"));
            }
            if (!reader.IsDBNull(permissionIdOrdinal))
            {
                var permission = new AccountPermissionDetailsItem(
                    reader.GetInt32(permissionIdOrdinal),
                    reader.GetString("permission_code"),
                    reader.GetString("permission_module"),
                    reader.GetString("permission_action"));
                account.Permissions.TryAdd(permission.Id, permission);
            }
        }

        return account?.ToResult();
    }

    public Task<AccountListResult> FindByEmailsAsync(
        IReadOnlyList<string>? rawEmails,
        CancellationToken cancellationToken)
    {
        var emails = (rawEmails ?? [])
            .SelectMany(value => value.Split(
                [',', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(CommonInputRules.NormalizeEmail)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (emails.Length == 0)
        {
            throw new AccountValidationException(
                "At least one email query parameter is required.");
        }

        return QueryAsync(emails, null, cancellationToken);
    }

    private async Task<AccountListResult> QueryAsync(
        IReadOnlyList<string> emails,
        int? filterAccountId,
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
        var emailParameters = emails
            .Select((_, index) => $"@email{index}")
            .ToArray();
        var whereClause = filterAccountId.HasValue
            ? "WHERE a.id = @accountId"
            : emailParameters.Length == 0
                ? string.Empty
                : $"WHERE a.email IN ({string.Join(", ", emailParameters)})";
        await using var command = new MySqlCommand(
            $"""
            SELECT
                a.id AS account_id,
                a.email,
                a.status AS account_status,
                a.last_login_at,
                u.display_name,
                r.id AS role_id,
                r.name AS role_name
            FROM accounts AS a
            LEFT JOIN users AS u ON u.account_id = a.id
            LEFT JOIN account_roles AS ar ON ar.account_id = a.id
            LEFT JOIN roles AS r ON r.id = ar.role_id
            {whereClause}
            ORDER BY a.created_at DESC, a.id DESC, r.name;
            """,
            connection);
        for (var index = 0; index < emails.Count; index++)
        {
            command.Parameters.AddWithValue(emailParameters[index], emails[index]);
        }
        if (filterAccountId.HasValue)
        {
            command.Parameters.AddWithValue("@accountId", filterAccountId.Value);
        }

        var accounts = new Dictionary<int, AccountListAccumulator>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var displayNameOrdinal = reader.GetOrdinal("display_name");
        var lastLoginOrdinal = reader.GetOrdinal("last_login_at");
        var roleIdOrdinal = reader.GetOrdinal("role_id");

        while (await reader.ReadAsync(cancellationToken))
        {
            var accountId = reader.GetInt32("account_id");
            if (!accounts.TryGetValue(accountId, out var account))
            {
                account = new AccountListAccumulator(
                    accountId,
                    reader.GetString("email"),
                    Convert.ToInt32(reader.GetValue(reader.GetOrdinal("account_status"))),
                    reader.IsDBNull(lastLoginOrdinal) ? null : reader.GetDateTime(lastLoginOrdinal),
                    reader.IsDBNull(displayNameOrdinal) ? null : reader.GetString(displayNameOrdinal));
                accounts.Add(accountId, account);
            }

            if (!reader.IsDBNull(roleIdOrdinal))
            {
                var role = new AccountRoleListItem(
                    reader.GetInt32(roleIdOrdinal),
                    reader.GetString("role_name"));
                account.Roles.TryAdd(role.Id, role);
            }
        }

        var items = accounts.Values
            .Select(account => account.ToResult())
            .ToArray();
        return new AccountListResult(items.Length, items);
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

internal sealed class AccountListAccumulator(
    int accountId,
    string email,
    int status,
    DateTime? lastLoginAt,
    string? displayName)
{
    public Dictionary<int, AccountRoleListItem> Roles { get; } = [];

    public AccountListItem ToResult() => new(
        accountId,
        email,
        displayName,
        status,
        lastLoginAt,
        Roles.Values
            .Select(role => role.Name)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray());
}

internal sealed record AccountListResult(
    int Total,
    IReadOnlyList<AccountListItem> Items);

internal sealed record AccountListItem(
    int AccountId,
    string Email,
    string? DisplayName,
    int Status,
    DateTime? LastLoginAt,
    IReadOnlyList<string> Roles);

internal sealed record AccountRoleListItem(
    int Id,
    string Name);

internal sealed class AccountDetailsAccumulator(
    int accountId,
    string email,
    string? firstName,
    string? lastName,
    string? displayName,
    int status,
    int? userStatus,
    int securityVersion,
    DateTime? lastLoginAt)
{
    public Dictionary<int, string> Roles { get; } = [];
    public Dictionary<int, AccountPermissionDetailsItem> Permissions { get; } = [];

    public AccountDetailsResult ToResult() => new(
        accountId,
        email,
        firstName,
        lastName,
        displayName,
        status,
        userStatus,
        securityVersion,
        lastLoginAt,
        Roles.Values.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        Permissions.Values
            .OrderBy(permission => permission.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray());
}

internal sealed record AccountDetailsResult(
    int AccountId,
    string Email,
    string? FirstName,
    string? LastName,
    string? DisplayName,
    int Status,
    int? UserStatus,
    int SecurityVersion,
    DateTime? LastLoginAt,
    IReadOnlyList<string> Roles,
    IReadOnlyList<AccountPermissionDetailsItem> Permissions);

internal sealed record AccountPermissionDetailsItem(
    int Id,
    string Code,
    string Module,
    string Action);
