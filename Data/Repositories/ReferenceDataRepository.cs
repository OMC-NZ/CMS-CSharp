using MySqlConnector;

namespace CMS_CSharp.Data.Repositories;

internal sealed class ReferenceDataRepository : IReferenceDataRepository
{
    public async Task<IReadOnlySet<string>> FindExistingChannelCodesAsync(
        MySqlConnection connection, MySqlTransaction? transaction,
        IReadOnlyList<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = ParameterNames("code", codes.Count);
        await using var command = new MySqlCommand(
            $"SELECT code FROM Channels WHERE code IN ({string.Join(", ", names)});", connection, transaction);
        AddParameters(command, names, codes);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetDeviceChannelCodesByModelsAsync(
        MySqlConnection connection, MySqlTransaction? transaction, IReadOnlyList<string> models,
        IReadOnlyList<string> selectedChannelCodes, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        if (models.Count == 0 || selectedChannelCodes.Count == 0)
            return new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var modelNames = ParameterNames("model", models.Count);
        var channelNames = ParameterNames("channel", selectedChannelCodes.Count);
        await using var command = new MySqlCommand(
            $"SELECT DISTINCT model, channel_code FROM Devices WHERE model IN ({string.Join(", ", modelNames)}) AND channel_code IN ({string.Join(", ", channelNames)});",
            connection, transaction);
        AddParameters(command, modelNames, models);
        AddParameters(command, channelNames, selectedChannelCodes);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var model = reader.GetString(0);
            if (!result.TryGetValue(model, out var codes)) result[model] = codes = [];
            codes.Add(reader.GetString(1));
        }
        return result.ToDictionary(x => x.Key, x => (IReadOnlyList<string>)x.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<IReadOnlyDictionary<string, int>> FindGiftIdsByAliasesAsync(
        MySqlConnection connection, MySqlTransaction? transaction,
        IReadOnlyList<string> aliases, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (aliases.Count == 0) return result;
        var names = ParameterNames("alias", aliases.Count);
        await using var command = new MySqlCommand(
            $"SELECT id, alias FROM Gifts WHERE alias IN ({string.Join(", ", names)});", connection, transaction);
        AddParameters(command, names, aliases);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result[reader.GetString(1)] = reader.GetInt32(0);
        return result;
    }

    public async Task<bool> ChannelExistsByCodeAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        string code,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT EXISTS(
                SELECT 1
                FROM Channels
                WHERE code = @code
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@code", code.Trim());

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public async Task<IReadOnlyList<string>> GetDeviceChannelCodesByModelAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        string model,
        IReadOnlyList<string> selectedChannelCodes,
        CancellationToken cancellationToken)
    {
        if (selectedChannelCodes.Count == 0)
        {
            return [];
        }

        var channelParameters = selectedChannelCodes
            .Select((_, index) => $"@channel{index}")
            .ToArray();
        await using var command = new MySqlCommand(
            $"""
            SELECT DISTINCT channel_code
            FROM Devices
            WHERE model = @model
              AND channel_code IN ({string.Join(", ", channelParameters)});
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@model", model.Trim());
        for (var index = 0; index < selectedChannelCodes.Count; index++)
        {
            command.Parameters.AddWithValue(channelParameters[index], selectedChannelCodes[index]);
        }

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task<int?> FindGiftIdByAliasAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        string alias,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT id
            FROM Gifts
            WHERE alias = @alias
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@alias", alias.Trim());

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : Convert.ToInt32(result);
    }

    private static string[] ParameterNames(string prefix, int count) =>
        Enumerable.Range(0, count).Select(index => $"@{prefix}{index}").ToArray();

    private static void AddParameters(MySqlCommand command, IReadOnlyList<string> names,
        IReadOnlyList<string> values)
    {
        for (var index = 0; index < names.Count; index++)
            command.Parameters.AddWithValue(names[index], values[index]);
    }
}
