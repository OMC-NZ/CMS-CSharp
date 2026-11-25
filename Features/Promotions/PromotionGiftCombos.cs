using System.Text.Json.Serialization;
using CMS_CSharp.Features.Claims;
using MySqlConnector;

namespace CMS_CSharp.Features.Promotions;

internal sealed record PromotionGiftComboInput(
    [property: JsonPropertyName("gift_comb")] int GiftComb,
    IReadOnlyList<PromotionGiftInput>? Gifts);

internal sealed record PromotionGiftComboResult(
    [property: JsonPropertyName("gift_comb")] int GiftComb,
    IReadOnlyList<PromotionComboGiftResult> Gifts);

internal sealed record PromotionComboGiftResult(int Id, string Name, string Alias, string? Color);

internal static class PromotionGiftCombos
{
    public static async Task<IReadOnlyDictionary<int, IReadOnlyList<PromotionGiftComboResult>>> LoadAsync(
        MySqlConnection connection, IReadOnlyList<int> promotionIds,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<int, IReadOnlyList<PromotionGiftComboResult>>();
        if (promotionIds.Count == 0) return result;
        var names = promotionIds.Select((_, i) => $"@promotion{i}").ToArray();
        await using var command = new MySqlCommand($"""
            SELECT DISTINCT pg.promotion_id, pg.gift_comb, g.id, g.name, g.alias, g.color
            FROM Promotion_Gifts pg INNER JOIN Gifts g ON g.id = pg.gift_id
            WHERE pg.promotion_id IN ({string.Join(',', names)})
            ORDER BY pg.promotion_id, pg.gift_comb, g.id;
            """, connection);
        for (var i = 0; i < names.Length; i++) command.Parameters.AddWithValue(names[i], promotionIds[i]);
        var rows = new List<(int PromotionId, int Combo, PromotionComboGiftResult Gift)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            rows.Add((reader.GetInt32(0), reader.GetInt32(1), new PromotionComboGiftResult(
                reader.GetInt32(2), reader.GetString(3), reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5))));
        foreach (var promotion in rows.GroupBy(x => x.PromotionId))
            result[promotion.Key] = promotion.GroupBy(x => x.Combo)
                .Select(group => new PromotionGiftComboResult(group.Key, group.Select(x => x.Gift).ToArray())).ToArray();
        return result;
    }

    public static async Task ValidateClaimSelectionAsync(
        MySqlConnection connection, MySqlTransaction? transaction, int promotionId,
        IReadOnlyList<int> giftIds, CancellationToken cancellationToken)
    {
        if (promotionId == 0) return;
        await using var command = new MySqlCommand(
            "SELECT gift_comb, gift_id FROM Promotion_Gifts WHERE promotion_id = @promotionId;",
            connection, transaction);
        command.Parameters.AddWithValue("@promotionId", promotionId);
        var combos = new Dictionary<int, HashSet<int>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var combo = reader.GetInt32(0);
            if (!combos.TryGetValue(combo, out var ids)) combos[combo] = ids = [];
            ids.Add(reader.GetInt32(1));
        }
        if (!combos.Values.Any(ids => ids.SetEquals(giftIds)))
            throw new ClaimValidationException(
                "Select all gifts from exactly one complete promotion gift combination. Mixing combinations or selecting only part of a combination is not allowed.");
    }
}
