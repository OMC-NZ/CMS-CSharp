using System.Text.Json.Serialization;

namespace CMS_CSharp.Contracts.Devices;

public sealed record DeviceSearchResult(
    [property: JsonPropertyName("market_name")] string MarketName,
    [property: JsonPropertyName("model")] string Model);
