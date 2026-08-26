using System.Text.Json.Serialization;

namespace CMS_CSharp.Contracts.Channels;

public sealed record ChannelListResult(
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("category")] string Category);
