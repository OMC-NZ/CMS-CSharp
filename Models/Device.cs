namespace CMS_CSharp.Models;

[Table("Devices")]
public class Device
{
    [Key, Column("imei"), StringLength(15)]
    public string Imei { get; set; } = string.Empty;

    [Column("model"), StringLength(7)]
    public string Model { get; set; } = string.Empty;

    [Column("category")]
    public sbyte Category { get; set; }

    [Column("market_name"), StringLength(45)]
    public string MarketName { get; set; } = string.Empty;

    [Column("color"), StringLength(45)]
    public string Color { get; set; } = string.Empty;

    [Column("channel_code"), StringLength(4)]
    public string ChannelCode { get; set; } = string.Empty;

    [Column("redemption_status")]
    public bool RedemptionStatus { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
