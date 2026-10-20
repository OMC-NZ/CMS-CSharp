namespace CMS_CSharp.Contracts.Gifts;

public sealed record GiftListResult(
    string Name,
    string Alias,
    string Color,
    sbyte Status,
    DateTime? UpdatedAt);
