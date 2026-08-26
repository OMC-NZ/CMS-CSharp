using System.Text.Json.Serialization;

namespace CMS_CSharp.Contracts.Channels;

public sealed record ChannelSearchResult(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("category")] string Category);
