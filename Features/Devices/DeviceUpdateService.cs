using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Features.Devices;

internal sealed partial class DeviceUpdateService(IConfiguration configuration)
{
    public async Task<DeviceUpdateResult?> UpdateAsync(
        string imei,
        DeviceUpdateRequest request,
        CancellationToken cancellationToken)
    {
        var normalizedImei = imei.Trim();
        if (!ImeiRegex().IsMatch(normalizedImei))
        {
            throw new DeviceUpdateValidationException(
                "imei must contain exactly 15 digits.");
        }

        var channelCode = request.ChannelCode?.Trim().ToUpperInvariant();
        if (request.ChannelCode is not null && string.IsNullOrWhiteSpace(channelCode))
        {
            throw new DeviceUpdateValidationException(
                "channelCode must not be empty when provided.");
        }
        if (channelCode is { Length: > 4 })
        {
            throw new DeviceUpdateValidationException(
                "channelCode must not exceed 4 characters.");
        }
        if (request.Category is < sbyte.MinValue or > sbyte.MaxValue)
        {
            throw new DeviceUpdateValidationException(
                "category must be an integer between -128 and 127.");
        }
        if (request.Category is null && channelCode is null)
        {
            throw new DeviceUpdateValidationException(
                "At least one of category or channelCode is required.");
        }

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured.");
        }

        await using var connection = new MySqlConnection(
            NormalizeMySqlConnectionString(connectionString));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            if (!await DeviceExistsAsync(
                    connection, transaction, normalizedImei, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            if (channelCode is not null &&
                !await ChannelExistsAsync(
                    connection, transaction, channelCode, cancellationToken))
            {
                throw new DeviceUpdateValidationException(
                    $"Channel code '{channelCode}' was not found.");
            }

            var assignments = new List<string>();
            var updatedFields = new List<string>();
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;

            if (request.Category.HasValue)
            {
                assignments.Add("category = @category");
                command.Parameters.AddWithValue("@category", request.Category.Value);
                updatedFields.Add("category");
            }
            if (channelCode is not null)
            {
                assignments.Add("channel_code = @channelCode");
                command.Parameters.AddWithValue("@channelCode", channelCode);
                updatedFields.Add("channelCode");
            }
            assignments.Add("updated_at = CURRENT_TIMESTAMP");
            command.Parameters.AddWithValue("@imei", normalizedImei);
            command.CommandText = $"""
                UPDATE Devices
                SET {string.Join(", ", assignments)}
                WHERE imei = @imei;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new DeviceUpdateResult(true, normalizedImei, updatedFields);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<bool> DeviceExistsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string imei,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT 1 FROM Devices WHERE imei = @imei LIMIT 1;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@imei", imei);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task<bool> ChannelExistsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string channelCode,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT 1 FROM Channels WHERE code = @channelCode LIMIT 1;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@channelCode", channelCode);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
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

    [GeneratedRegex(@"^\d{15}$", RegexOptions.CultureInvariant)]
    private static partial Regex ImeiRegex();

    [GeneratedRegex(@"(?i)Server=([^;,]+),(\d+)")]
    private static partial Regex ConnectionPortRegex();
}

internal sealed record DeviceUpdateRequest(
    int? Category,
    string? ChannelCode);

internal sealed record DeviceUpdateResult(
    bool Success,
    string Imei,
    IReadOnlyList<string> UpdatedFields);

internal sealed class DeviceUpdateValidationException(string message)
    : Exception(message);
