namespace CMS_CSharp.Models;

[Table("Event_Claims")]
public class EventClaim
{
    [Key, Column("id"), StringLength(255)]
    public string Id { get; set; } = string.Empty;

    [Column("event_id")]
    public int EventId { get; set; }

    [Column("imei"), StringLength(15)]
    public string? Imei { get; set; }

    [Column("customer_id")]
    public int CustomerId { get; set; }

    [Column("status")]
    public sbyte Status { get; set; }

    [Column("extra_data", TypeName = "longtext")]
    public string? ExtraData { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
