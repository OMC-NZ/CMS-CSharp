namespace CMS_CSharp.Contracts.Gifts;

public sealed record CreateGiftRequest(
    string? Name,
    string? Alias,
    string? Color);
