namespace CMS_CSharp.Contracts.Gifts;

public sealed record UpdateGiftRequest(
    string? Name,
    string? Alias,
    string? Color,
    int? Status);
