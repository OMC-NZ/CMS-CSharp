using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Features.Devices;

internal sealed partial class DeviceImportService(IConfiguration configuration)
{
    private const int MaxItems = 100_000;
    private const int BatchSize = 500;
    private const int MaxReportedErrors = 100;

    public async Task<DeviceImportResult> ImportAsync(
        DeviceImportRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Devices is null || request.Devices.Count == 0)
        {
            throw new DeviceImportValidationException(
                "The devices array must contain at least one item.");
        }
        if (request.Devices.Count > MaxItems)
        {
            throw new DeviceImportValidationException(
                $"The devices array must not contain more than {MaxItems:N0} items.");
        }

        var parsed = ValidateItems(request.Devices);
        if (parsed.ValidDevices.Count == 0)
        {
            return new DeviceImportResult(
                request.Devices.Count, 0, 0, 0, parsed.DuplicateItems,
                parsed.InvalidItems, parsed.Errors, parsed.ErrorsTruncated);
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
            var existingImeis = await LoadExistingImeisAsync(
                connection, transaction, parsed.ValidDevices.Keys, cancellationToken);
            var devicesToInsert = parsed.ValidDevices.Values
                .Where(device => !existingImeis.Contains(device.Imei))
                .ToArray();

            var insertedItems = 0;
            foreach (var categoryGroup in devicesToInsert.GroupBy(
                         device => device.Category.HasValue))
            {
                foreach (var batch in categoryGroup.Chunk(BatchSize))
                {
                    insertedItems += await InsertBatchAsync(
                        connection, transaction, batch, categoryGroup.Key,
                        cancellationToken);
                }
            }

            await transaction.CommitAsync(cancellationToken);

            return new DeviceImportResult(
                request.Devices.Count,
                parsed.ValidDevices.Count,
                insertedItems,
                parsed.ValidDevices.Count - insertedItems,
                parsed.DuplicateItems,
                parsed.InvalidItems,
                parsed.Errors,
                parsed.ErrorsTruncated);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static ParsedItems ValidateItems(IReadOnlyList<DeviceImportItem?> items)
    {
        var devices = new Dictionary<string, ImportedDevice>(StringComparer.Ordinal);
        var errors = new List<DeviceImportItemError>();
        var invalidItems = 0;
        var duplicateItems = 0;

        for (var index = 0; index < items.Count; index++)
        {
            var itemNumber = index + 1;
            var item = items[index];
            if (item is null)
            {
                invalidItems++;
                AddError(errors, itemNumber, string.Empty, "Device item is required.");
                continue;
            }

            var imei = item.Imei?.Trim() ?? string.Empty;
            var marketName = NormalizeMarketName(item.MarketName ?? string.Empty);
            var model = item.Model?.Trim() ?? string.Empty;
            var color = item.Color?.Trim() ?? string.Empty;
            var error = ValidateItem(imei, marketName, model, color, item.Category);
            if (error is not null)
            {
                invalidItems++;
                AddError(errors, itemNumber, imei, error);
                continue;
            }

            if (!devices.TryAdd(
                    imei,
                    new ImportedDevice(imei, model, marketName, color, item.Category)))
            {
                duplicateItems++;
            }
        }

        return new ParsedItems(
            devices, invalidItems, duplicateItems, errors,
            invalidItems > errors.Count);
    }

    private static void AddError(
        ICollection<DeviceImportItemError> errors,
        int index,
        string imei,
        string message)
    {
        if (errors.Count < MaxReportedErrors)
        {
            errors.Add(new DeviceImportItemError(index, imei, message));
        }
    }

    private static string NormalizeMarketName(string value)
    {
        var normalized = value.Trim();
        return normalized.StartsWith("OPPO ", StringComparison.OrdinalIgnoreCase)
            ? normalized[5..].Trim()
            : normalized;
    }

    private static string? ValidateItem(
        string imei, string marketName, string model, string color, int? category)
    {
        if (!ValidImeiRegex().IsMatch(imei))
        {
            return "IMEI must start with 86 and contain exactly 15 digits.";
        }
        if (marketName.Length == 0)
        {
            return "marketName is required.";
        }
        if (marketName.Length > 45)
        {
            return "marketName exceeds 45 characters.";
        }
        if (model.Length == 0)
        {
            return "model is required.";
        }
        if (model.Length > 7)
        {
            return "model exceeds 7 characters.";
        }
        if (color.Length == 0)
        {
            return "color is required.";
        }
        if (color.Length > 45)
        {
            return "color exceeds 45 characters.";
        }
        if (category is < sbyte.MinValue or > sbyte.MaxValue)
        {
            return "category must be an integer between -128 and 127.";
        }
        return null;
    }

    private static async Task<HashSet<string>> LoadExistingImeisAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        IEnumerable<string> imeis,
        CancellationToken cancellationToken)
    {
        var existing = new HashSet<string>(StringComparer.Ordinal);
        foreach (var batch in imeis.Chunk(1_000))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            var parameterNames = new string[batch.Length];
            for (var index = 0; index < batch.Length; index++)
            {
                parameterNames[index] = $"@imei{index}";
                command.Parameters.AddWithValue(parameterNames[index], batch[index]);
            }
            command.CommandText =
                $"SELECT imei FROM Devices WHERE imei IN ({string.Join(',', parameterNames)});";

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                existing.Add(reader.GetString(0));
            }
        }
        return existing;
    }

    private static async Task<int> InsertBatchAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        IReadOnlyList<ImportedDevice> devices,
        bool includeCategory,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var values = new string[devices.Count];

        for (var index = 0; index < devices.Count; index++)
        {
            values[index] = includeCategory
                ? $"(@imei{index}, @model{index}, @marketName{index}, @color{index}, @category{index}, 0)"
                : $"(@imei{index}, @model{index}, @marketName{index}, @color{index}, 0)";
            command.Parameters.AddWithValue($"@imei{index}", devices[index].Imei);
            command.Parameters.AddWithValue($"@model{index}", devices[index].Model);
            command.Parameters.AddWithValue($"@marketName{index}", devices[index].MarketName);
            command.Parameters.AddWithValue($"@color{index}", devices[index].Color);
            if (includeCategory)
            {
                command.Parameters.AddWithValue(
                    $"@category{index}", devices[index].Category!.Value);
            }
        }

        var columns = includeCategory
            ? "imei, model, market_name, color, category, redemption_status"
            : "imei, model, market_name, color, redemption_status";
        command.CommandText = $"""
            INSERT INTO Devices ({columns})
            VALUES {string.Join(',', values)}
            ON DUPLICATE KEY UPDATE imei = VALUES(imei);
            """;
        return await command.ExecuteNonQueryAsync(cancellationToken);
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

    [GeneratedRegex(@"^86\d{13}$", RegexOptions.CultureInvariant)]
    private static partial Regex ValidImeiRegex();

    [GeneratedRegex(@"(?i)Server=([^;,]+),(\d+)")]
    private static partial Regex ConnectionPortRegex();

    private sealed record ImportedDevice(
        string Imei, string Model, string MarketName, string Color, int? Category);

    private sealed record ParsedItems(
        Dictionary<string, ImportedDevice> ValidDevices,
        int InvalidItems,
        int DuplicateItems,
        IReadOnlyList<DeviceImportItemError> Errors,
        bool ErrorsTruncated);
}

internal sealed record DeviceImportRequest(
    IReadOnlyList<DeviceImportItem?>? Devices);

internal sealed record DeviceImportItem(
    string? Imei,
    string? Model,
    string? MarketName,
    string? Color,
    int? Category);

internal sealed record DeviceImportResult(
    int ItemsReceived,
    int ValidUniqueItems,
    int InsertedItems,
    int ExistingItems,
    int DuplicateItemsInRequest,
    int InvalidItems,
    IReadOnlyList<DeviceImportItemError> Errors,
    bool ErrorsTruncated);

internal sealed record DeviceImportItemError(
    int Index,
    string Imei,
    string Message);

internal sealed class DeviceImportValidationException : Exception
{
    public DeviceImportValidationException(string message) : base(message)
    {
    }
}
