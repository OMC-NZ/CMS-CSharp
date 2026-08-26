using System.Text.Json.Serialization;

namespace CMS_CSharp.Contracts.Gifts;

public sealed record GiftSearchResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("alias")] string Alias,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("status")] sbyte Status);
