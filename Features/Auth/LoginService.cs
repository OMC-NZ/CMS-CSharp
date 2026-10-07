using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CMS_CSharp.Models.Auth;
using CMS_CSharp.Validation;
using Microsoft.AspNetCore.Identity;
using MySqlConnector;

namespace CMS_CSharp.Features.Auth;

internal sealed partial class LoginService(IConfiguration configuration)
{
    private readonly PasswordHasher<Account> _passwordHasher = new();

    public async Task<LoginResult> LoginAsync(
        LoginCommand request,
        string? ipAddress,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
        {
            throw new LoginValidationException("email and password are required.");
        }

        var email = CommonInputRules.NormalizeEmail(request.Email);
        var settings = ReadTokenSettings();
        var connectionString = configuration.GetConnectionString("AuthConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:AuthConnection is not configured.");
        }

        await using var connection = new MySqlConnection(
            NormalizeMySqlConnectionString(connectionString));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var loginData = await FindLoginAsync(
                connection, transaction, email, cancellationToken);
            if (loginData is null)
            {
                throw new LoginUnauthorizedException("Invalid email or password.");
            }

            var account = new Account
            {
                Id = loginData.AccountId,
                Email = loginData.Email,
                PasswordHash = loginData.PasswordHash
            };
            var passwordResult = _passwordHasher.VerifyHashedPassword(
                account,
                loginData.PasswordHash,
                request.Password);
            if (passwordResult == PasswordVerificationResult.Failed)
            {
                throw new LoginUnauthorizedException("Invalid email or password.");
            }
            if (loginData.AccountStatus != 1 || loginData.UserStatus != 1)
            {
                throw new LoginForbiddenException("This account is disabled.");
            }

            var access = await ReadAccessAsync(
                connection, transaction, loginData.AccountId, cancellationToken);
            var sessionId = Guid.NewGuid().ToString();
            var refreshToken = GenerateRefreshToken();
            var refreshTokenHash = HashToken(refreshToken);
            var now = DateTime.UtcNow;
            var accessExpiresAt = now.AddMinutes(settings.AccessTokenMinutes);
            var refreshExpiresAt = now.AddDays(settings.RefreshTokenDays);

            var accessToken = CreateAccessToken(
                settings,
                sessionId,
                loginData,
                access,
                now,
                accessExpiresAt);

            await InsertSessionAsync(
                connection,
                transaction,
                sessionId,
                loginData.AccountId,
                refreshTokenHash,
                loginData.SecurityVersion,
                ipAddress,
                userAgent,
                refreshExpiresAt,
                cancellationToken);
            await UpdateLastLoginAsync(
                connection,
                transaction,
                loginData.AccountId,
                passwordResult == PasswordVerificationResult.SuccessRehashNeeded
                    ? _passwordHasher.HashPassword(account, request.Password)
                    : null,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new LoginResult(
                true,
                accessToken,
                refreshToken,
                "Bearer",
                accessExpiresAt,
                new LoginUserResult(
                    loginData.UserId,
                    loginData.AccountId,
                    loginData.Email,
                    loginData.FirstName,
                    loginData.LastName,
                    loginData.DisplayName),
                access.Roles,
                access.Permissions);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private TokenSettings ReadTokenSettings()
    {
        var secret = configuration["JWT_SECRET"];
        if (string.IsNullOrWhiteSpace(secret) || Encoding.UTF8.GetByteCount(secret) < 32)
        {
            throw new InvalidOperationException(
                "JWT_SECRET must be configured with at least 32 bytes.");
        }

        var issuer = configuration["JWT_ISSUER"];
        var audience = configuration["JWT_AUDIENCE"];
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience))
        {
            throw new InvalidOperationException(
                "JWT_ISSUER and JWT_AUDIENCE must be configured.");
        }

        var accessTokenMinutes = configuration.GetValue<int?>("JWT_ACCESS_TOKEN_MINUTES") ?? 480;
        var refreshTokenDays = configuration.GetValue<int?>("JWT_REFRESH_TOKEN_DAYS") ?? 7;
        if (accessTokenMinutes is < 1 or > 1440 || refreshTokenDays is < 1 or > 90)
        {
            throw new InvalidOperationException(
                "JWT token lifetimes are outside their allowed ranges.");
        }

        return new TokenSettings(
            secret,
            issuer,
            audience,
            accessTokenMinutes,
            refreshTokenDays);
    }

    private static async Task<LoginData?> FindLoginAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string email,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT
                a.id AS account_id,
                a.email,
                a.pwd_hash,
                a.status AS account_status,
                a.security_version,
                u.id AS user_id,
                u.first_name,
                u.last_name,
                u.display_name,
                u.status AS user_status
            FROM accounts AS a
            INNER JOIN users AS u ON u.account_id = a.id
            WHERE a.email = @email
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@email", email);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        var displayNameOrdinal = reader.GetOrdinal("display_name");
        var firstName = reader.GetString("first_name");
        var lastName = reader.GetString("last_name");
        return new LoginData(
            reader.GetInt32("account_id"),
            reader.GetInt32("user_id"),
            reader.GetString("email"),
            reader.GetString("pwd_hash"),
            reader.GetByte("account_status"),
            reader.GetByte("user_status"),
            reader.GetInt32("security_version"),
            firstName,
            lastName,
            reader.IsDBNull(displayNameOrdinal)
                ? $"{firstName} {lastName}"
                : reader.GetString(displayNameOrdinal));
    }

    private static async Task<LoginAccess> ReadAccessAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int accountId,
        CancellationToken cancellationToken)
    {
        var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = new MySqlCommand(
            """
            SELECT DISTINCT
                r.code AS role_code,
                p.code AS permission_code
            FROM account_roles AS ar
            INNER JOIN roles AS r
                ON r.id = ar.role_id
               AND r.status = 1
            LEFT JOIN role_permissions AS rp ON rp.role_id = r.id
            LEFT JOIN permissions AS p
                ON p.id = rp.permission_id
               AND p.status = 1
            WHERE ar.account_id = @accountId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@accountId", accountId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var permissionOrdinal = reader.GetOrdinal("permission_code");
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(reader.GetString("role_code"));
            if (!reader.IsDBNull(permissionOrdinal))
            {
                permissions.Add(reader.GetString(permissionOrdinal));
            }
        }

        return new LoginAccess(
            roles.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
            permissions.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task InsertSessionAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string sessionId,
        int accountId,
        string refreshTokenHash,
        int securityVersion,
        string? ipAddress,
        string? userAgent,
        DateTime refreshExpiresAt,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            INSERT INTO sessions
                (id, account_id, refresh_token_hash, security_version,
                 ip_address, user_agent, expires_at, revoked_at,
                 revoked_by_account_id, created_at)
            VALUES
                (@id, @accountId, @refreshTokenHash, @securityVersion,
                 @ipAddress, @userAgent, @expiresAt, NULL, NULL, CURRENT_TIMESTAMP);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@id", sessionId);
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.AddWithValue("@refreshTokenHash", refreshTokenHash);
        command.Parameters.AddWithValue("@securityVersion", securityVersion);
        command.Parameters.AddWithValue("@ipAddress", (object?)ipAddress ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "@userAgent",
            string.IsNullOrEmpty(userAgent)
                ? DBNull.Value
                : userAgent[..Math.Min(userAgent.Length, 500)]);
        command.Parameters.AddWithValue("@expiresAt", refreshExpiresAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateLastLoginAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int accountId,
        string? replacementPasswordHash,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.Parameters.AddWithValue("@accountId", accountId);
        if (replacementPasswordHash is null)
        {
            command.CommandText = """
                UPDATE accounts
                SET last_login_at = CURRENT_TIMESTAMP,
                    updated_at = CURRENT_TIMESTAMP
                WHERE id = @accountId;
                """;
        }
        else
        {
            command.CommandText = """
                UPDATE accounts
                SET pwd_hash = @passwordHash,
                    last_login_at = CURRENT_TIMESTAMP,
                    updated_at = CURRENT_TIMESTAMP
                WHERE id = @accountId;
                """;
            command.Parameters.AddWithValue("@passwordHash", replacementPasswordHash);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string CreateAccessToken(
        TokenSettings settings,
        string sessionId,
        LoginData loginData,
        LoginAccess access,
        DateTime issuedAt,
        DateTime expiresAt)
    {
        var header = new { alg = "HS256", typ = "JWT" };
        var payload = new
        {
            sub = loginData.AccountId.ToString(),
            user_id = loginData.UserId,
            sid = sessionId,
            email = loginData.Email,
            name = loginData.DisplayName,
            security_version = loginData.SecurityVersion,
            roles = access.Roles,
            permissions = access.Permissions,
            iss = settings.Issuer,
            aud = settings.Audience,
            iat = new DateTimeOffset(issuedAt).ToUnixTimeSeconds(),
            exp = new DateTimeOffset(expiresAt).ToUnixTimeSeconds()
        };

        var encodedHeader = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header));
        var encodedPayload = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var unsignedToken = $"{encodedHeader}.{encodedPayload}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(settings.Secret));
        var signature = hmac.ComputeHash(Encoding.ASCII.GetBytes(unsignedToken));
        return $"{unsignedToken}.{Base64UrlEncode(signature)}";
    }

    private static string GenerateRefreshToken() =>
        Base64UrlEncode(RandomNumberGenerator.GetBytes(48));

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Base64UrlEncode(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

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

internal sealed record LoginData(
    int AccountId,
    int UserId,
    string Email,
    string PasswordHash,
    byte AccountStatus,
    byte UserStatus,
    int SecurityVersion,
    string FirstName,
    string LastName,
    string DisplayName);

internal sealed record LoginAccess(
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

internal sealed record TokenSettings(
    string Secret,
    string Issuer,
    string Audience,
    int AccessTokenMinutes,
    int RefreshTokenDays);

internal sealed class LoginValidationException(string message) : Exception(message);
internal sealed class LoginUnauthorizedException(string message) : Exception(message);
internal sealed class LoginForbiddenException(string message) : Exception(message);
