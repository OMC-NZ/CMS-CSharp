namespace CMS_CSharp.Contracts.Gifts;

public sealed record GiftGetResult(
    int Id,
    string Name,
    string Alias,
    string Color,
    sbyte Status,
    DateTime? UpdatedAt);
