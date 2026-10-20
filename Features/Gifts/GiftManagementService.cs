using System.Text.RegularExpressions;
using CMS_CSharp.Contracts.Gifts;
using MySqlConnector;

namespace CMS_CSharp.Features.Gifts;

internal sealed partial class GiftManagementService(IConfiguration configuration)
{
    public async Task<IReadOnlyList<GiftSearchResult>> SearchByNameAsync(
        string? name,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeRequired(name, "name");
        return await ExecuteSearchAsync(
            "name LIKE CONCAT('%', @keyword, '%') ESCAPE '='",
            normalized,
            cancellationToken);
    }

    public async Task<IReadOnlyList<GiftSearchResult>> SearchAsync(
        string? keyword,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeRequired(keyword, "keyword");
        return await ExecuteSearchAsync(
            """
            (name LIKE CONCAT('%', @keyword, '%') ESCAPE '='
             OR alias LIKE CONCAT('%', @keyword, '%') ESCAPE '=')
            """,
            normalized,
            cancellationToken);
    }

    private async Task<IReadOnlyList<GiftSearchResult>> ExecuteSearchAsync(
        string trustedPredicate,
        string normalizedKeyword,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT id, name, alias, color, status, updated_at
            FROM Gifts
            WHERE {trustedPredicate}
            ORDER BY updated_at DESC, name, alias;
            """;
        command.Parameters.AddWithValue("@keyword", EscapeLikePattern(normalizedKeyword));

        var results = new List<GiftSearchResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new GiftSearchResult(
                reader.GetInt32("id"),
                reader.GetString("name"),
                reader.GetString("alias"),
                reader.GetString("color"),
                reader.GetSByte("status"),
                reader.IsDBNull(reader.GetOrdinal("updated_at"))
                    ? null
                    : reader.GetDateTime("updated_at")));
        }
        return results;
    }

    public async Task<GiftListResult> CreateAsync(
        CreateGiftRequest request,
        CancellationToken cancellationToken)
    {
        var name = NormalizeRequired(request.Name, "name");
        var alias = NormalizeRequired(request.Alias, "alias");
        var color = NormalizeRequired(request.Color, "color");
        ValidateLengths(name, alias, color);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await AliasExistsAsync(connection, alias, null, cancellationToken))
        {
            throw new GiftConflictException($"Gift alias '{alias}' already exists.");
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Gifts (name, alias, color, status, updated_at)
                VALUES (@name, @alias, @color, 0, CURRENT_TIMESTAMP);
                """;
            command.Parameters.AddWithValue("@name", name);
            command.Parameters.AddWithValue("@alias", alias);
            command.Parameters.AddWithValue("@color", color);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new GiftConflictException($"Gift alias '{alias}' already exists.");
        }

        return await GetByAliasAsync(connection, alias, cancellationToken)
            ?? throw new InvalidOperationException("The created Gift could not be read.");
    }

    public async Task<GiftListResult?> UpdateAsync(
        int giftId,
        UpdateGiftRequest request,
        CancellationToken cancellationToken)
    {
        if (giftId <= 0)
        {
            throw new GiftValidationException("id must be greater than zero.");
        }
        var name = request.Name is null ? null : NormalizeRequired(request.Name, "name");
        var alias = request.Alias is null ? null : NormalizeRequired(request.Alias, "alias");
        var color = request.Color is null ? null : NormalizeRequired(request.Color, "color");
        var status = ValidateStatus(request.Status);

        if (name is null && alias is null && color is null && status is null)
        {
            throw new GiftValidationException(
                "At least one of name, alias, color, or status is required.");
        }
        ValidateLengths(name, alias, color);

        await using var connection = await OpenConnectionAsync(cancellationToken);
        if (await GetByIdAsync(connection, giftId, cancellationToken) is null)
        {
            return null;
        }
        if (alias is not null &&
            await AliasExistsAsync(connection, alias, giftId, cancellationToken))
        {
            throw new GiftConflictException($"Gift alias '{alias}' already exists.");
        }

        var assignments = new List<string>();
        await using var command = connection.CreateCommand();
        if (name is not null)
        {
            assignments.Add("name = @name");
            command.Parameters.AddWithValue("@name", name);
        }
        if (alias is not null)
        {
            assignments.Add("alias = @alias");
            command.Parameters.AddWithValue("@alias", alias);
        }
        if (color is not null)
        {
            assignments.Add("color = @color");
            command.Parameters.AddWithValue("@color", color);
        }
        if (status.HasValue)
        {
            assignments.Add("status = @status");
            command.Parameters.AddWithValue("@status", status.Value);
        }
        assignments.Add("updated_at = CURRENT_TIMESTAMP");
        command.Parameters.AddWithValue("@giftId", giftId);
        command.CommandText = $"""
            UPDATE Gifts
            SET {string.Join(", ", assignments)}
            WHERE id = @giftId;
            """;

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (MySqlException exception) when (exception.Number == 1062)
        {
            throw new GiftConflictException(
                $"Gift alias '{alias}' already exists.");
        }

        return await GetByIdAsync(connection, giftId, cancellationToken);
    }

    public async Task<GiftDeleteResult?> DeleteAsync(
        int giftId,
        CancellationToken cancellationToken)
    {
        if (giftId <= 0)
        {
            throw new GiftValidationException("id must be greater than zero.");
        }
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            await using var findCommand = new MySqlCommand(
                "SELECT id FROM Gifts WHERE id = @giftId LIMIT 1 FOR UPDATE;",
                connection,
                transaction);
            findCommand.Parameters.AddWithValue("@giftId", giftId);
            var giftIdValue = await findCommand.ExecuteScalarAsync(cancellationToken);
            if (giftIdValue is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            await using var referenceCommand = new MySqlCommand(
                """
                SELECT EXISTS(
                    SELECT 1 FROM Promotion_Gifts WHERE gift_id = @giftId
                    UNION ALL
                    SELECT 1 FROM Claim_Gifts WHERE gift_id = @giftId
                );
                """,
                connection,
                transaction);
            referenceCommand.Parameters.AddWithValue("@giftId", giftId);
            if (Convert.ToInt32(
                    await referenceCommand.ExecuteScalarAsync(cancellationToken)) == 1)
            {
                throw new GiftConflictException(
                    $"Gift ID '{giftId}' is used by an existing Promotion or Claim and cannot be deleted.");
            }

            await using var deleteCommand = new MySqlCommand(
                "DELETE FROM Gifts WHERE id = @giftId;",
                connection,
                transaction);
            deleteCommand.Parameters.AddWithValue("@giftId", giftId);
            await deleteCommand.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new GiftDeleteResult(true, giftId);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<MySqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured.");
        }

        var connection = new MySqlConnection(
            NormalizeMySqlConnectionString(connectionString));
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private static async Task<GiftListResult?> GetByAliasAsync(
        MySqlConnection connection,
        string alias,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, alias, color, status, updated_at
            FROM Gifts
            WHERE alias = @alias
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@alias", alias);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadGift(reader) : null;
    }

    private static async Task<bool> AliasExistsAsync(
        MySqlConnection connection,
        string alias,
        int? excludedGiftId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = excludedGiftId is null
            ? "SELECT 1 FROM Gifts WHERE alias = @alias LIMIT 1;"
            : "SELECT 1 FROM Gifts WHERE alias = @alias AND id <> @excludedGiftId LIMIT 1;";
        command.Parameters.AddWithValue("@alias", alias);
        if (excludedGiftId is not null)
        {
            command.Parameters.AddWithValue("@excludedGiftId", excludedGiftId.Value);
        }
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<GiftListResult?> GetByIdAsync(
        MySqlConnection connection,
        int giftId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT name, alias, color, status, updated_at
            FROM Gifts
            WHERE id = @giftId
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("@giftId", giftId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadGift(reader) : null;
    }

    private static GiftListResult ReadGift(MySqlDataReader reader) =>
        new(
            reader.GetString("name"),
            reader.GetString("alias"),
            reader.GetString("color"),
            reader.GetSByte("status"),
            reader.IsDBNull(reader.GetOrdinal("updated_at"))
                ? null
                : reader.GetDateTime("updated_at"));

    private static string NormalizeRequired(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? throw new GiftValidationException($"{fieldName} is required.")
            : normalized;
    }

    private static int? ValidateStatus(int? status)
    {
        if (!status.HasValue)
        {
            return null;
        }
        if (status is < 0 or > 2)
        {
            throw new GiftValidationException("status must be 0, 1, or 2.");
        }
        return status;
    }

    private static void ValidateLengths(string? name, string? alias, string? color)
    {
        if (name is { Length: > 45 })
        {
            throw new GiftValidationException("name must not exceed 45 characters.");
        }
        if (alias is { Length: > 45 })
        {
            throw new GiftValidationException("alias must not exceed 45 characters.");
        }
        if (color is { Length: > 45 })
        {
            throw new GiftValidationException("color must not exceed 45 characters.");
        }
    }

    private static string EscapeLikePattern(string value) =>
        value.Replace("=", "==", StringComparison.Ordinal)
            .Replace("%", "=%", StringComparison.Ordinal)
            .Replace("_", "=_", StringComparison.Ordinal);

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

internal sealed class GiftValidationException(string message) : Exception(message);

internal sealed class GiftConflictException(string message) : Exception(message);

internal sealed record GiftDeleteResult(bool Success, int Id);
