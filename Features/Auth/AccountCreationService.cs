using System.Text.RegularExpressions;
using CMS_CSharp.Models.Auth;
using CMS_CSharp.Validation;
using Microsoft.AspNetCore.Identity;
using MySqlConnector;

namespace CMS_CSharp.Features.Auth;

internal sealed partial class AccountCreationService(IConfiguration configuration)
{
    private readonly PasswordHasher<Account> _passwordHasher = new();

    public async Task<CreateAccountResult> CreateAsync(
        CreateAccountCommand request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Email))
        {
            throw new AccountValidationException("email is required.");
        }
        if (string.IsNullOrWhiteSpace(request.FirstName))
        {
            throw new AccountValidationException("firstName is required.");
        }
        if (string.IsNullOrWhiteSpace(request.LastName))
        {
            throw new AccountValidationException("lastName is required.");
        }

        var email = CommonInputRules.NormalizeEmail(request.Email);
        var firstName = CommonInputRules.NormalizeTitle(request.FirstName, "firstName");
        var lastName = CommonInputRules.NormalizeTitle(request.LastName, "lastName");
        var displayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? $"{firstName} {lastName}"
            : CommonInputRules.NormalizeTitle(request.DisplayName, "displayName");
        ValidatePassword(request.Password);

        var roleCodes = (request.RoleCodes ?? [])
            .Select(roleCode => roleCode?.Trim().ToUpperInvariant() ?? string.Empty)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (roleCodes.Length == 0 || roleCodes.Any(string.IsNullOrWhiteSpace))
        {
            throw new AccountValidationException("At least one roleCode is required.");
        }
        if (roleCodes.Any(roleCode => roleCode.Length > 255))
        {
            throw new AccountValidationException(
                "Every roleCode must not exceed 255 characters.");
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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            if (await EmailExistsAsync(connection, transaction, email, cancellationToken))
            {
                throw new AccountConflictException("An account with this email already exists.");
            }

            var roles = await FindActiveRolesAsync(
                connection, transaction, roleCodes, cancellationToken);

            var account = new Account { Email = email };
            var passwordHash = _passwordHasher.HashPassword(account, request.Password);
            var accountId = await InsertAccountAsync(
                connection, transaction, email, passwordHash, cancellationToken);
            var userId = await InsertUserAsync(
                connection,
                transaction,
                accountId,
                firstName,
                lastName,
                displayName,
                cancellationToken);
            await InsertRolesAsync(
                connection,
                transaction,
                accountId,
                roles.Select(role => role.Id).ToArray(),
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new CreateAccountResult(
                true,
                accountId,
                userId,
                roles.Select(role => role.Code).ToArray());
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new AccountConflictException("An account with this email already exists.");
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static void ValidatePassword(string password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < 8)
        {
            throw new AccountValidationException(
                "password must contain at least 8 characters.");
        }
        if (password.Length > 128)
        {
            throw new AccountValidationException(
                "password must not exceed 128 characters.");
        }
        if (!password.Any(char.IsUpper) ||
            !password.Any(char.IsLower) ||
            !password.Any(char.IsDigit))
        {
            throw new AccountValidationException(
                "password must contain an uppercase letter, a lowercase letter, and a digit.");
        }
    }

    private static async Task<bool> EmailExistsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string email,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT 1 FROM accounts WHERE email = @email LIMIT 1;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@email", email);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<IReadOnlyList<AccountRoleLookup>> FindActiveRolesAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        IReadOnlyList<string> roleCodes,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var parameterNames = new string[roleCodes.Count];
        for (var index = 0; index < roleCodes.Count; index++)
        {
            parameterNames[index] = $"@roleCode{index}";
            command.Parameters.AddWithValue(parameterNames[index], roleCodes[index]);
        }

        command.CommandText = $"""
            SELECT id, code
            FROM roles
            WHERE status = 1
              AND code IN ({string.Join(", ", parameterNames)});
            """;

        var roles = new List<AccountRoleLookup>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(new AccountRoleLookup(
                reader.GetInt32("id"),
                reader.GetString("code")));
        }

        var foundCodes = roles
            .Select(role => role.Code)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missingCodes = roleCodes
            .Where(roleCode => !foundCodes.Contains(roleCode))
            .ToArray();
        if (missingCodes.Length > 0)
        {
            throw new AccountValidationException(
                $"Active roles were not found for roleCodes: {string.Join(", ", missingCodes)}.");
        }

        return roles
            .OrderBy(role => role.Code, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task<int> InsertAccountAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string email,
        string passwordHash,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            INSERT INTO accounts
                (email, pwd_hash, status, security_version, last_login_at,
                 disabled_at, disabled_by_account_id, created_at, updated_at)
            VALUES
                (@email, @passwordHash, 1, 1, NULL, NULL, NULL,
                 CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@email", email);
        command.Parameters.AddWithValue("@passwordHash", passwordHash);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return checked((int)command.LastInsertedId);
    }

    private static async Task<int> InsertUserAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int accountId,
        string firstName,
        string lastName,
        string displayName,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            INSERT INTO users
                (account_id, first_name, last_name, display_name, status,
                 created_at, updated_at)
            VALUES
                (@accountId, @firstName, @lastName, @displayName, 1,
                 CURRENT_TIMESTAMP, CURRENT_TIMESTAMP);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@accountId", accountId);
        command.Parameters.AddWithValue("@firstName", firstName);
        command.Parameters.AddWithValue("@lastName", lastName);
        command.Parameters.AddWithValue("@displayName", displayName);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return checked((int)command.LastInsertedId);
    }

    private static async Task InsertRolesAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int accountId,
        IReadOnlyList<int> roleIds,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            INSERT INTO account_roles
                (account_id, role_id, assigned_by_account_id, created_at)
            VALUES
                (@accountId, @roleId, NULL, CURRENT_TIMESTAMP);
            """,
            connection,
            transaction);
        command.Parameters.Add("@accountId", MySqlDbType.Int32).Value = accountId;
        var roleParameter = command.Parameters.Add("@roleId", MySqlDbType.Int32);

        foreach (var roleId in roleIds)
        {
            roleParameter.Value = roleId;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
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

internal sealed class AccountValidationException(string message) : Exception(message);

internal sealed class AccountConflictException(string message) : Exception(message);

internal sealed record AccountRoleLookup(int Id, string Code);
