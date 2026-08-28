using System.Text.Json.Serialization;

namespace CMS_CSharp.Contracts.Devices;

public sealed record DeviceSearchResult(
    [property: JsonPropertyName("market_name")] string MarketName,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("channels")] IReadOnlyList<DeviceSearchChannelResult> Channels);

public sealed record DeviceSearchChannelResult(
    [property: JsonPropertyName("channel_name")] string ChannelName,
    [property: JsonPropertyName("channel_code")] string ChannelCode);
