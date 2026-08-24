namespace CMS_CSharp.Models;

[Table("Event_Channels")]
public class EventChannel
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("event_id")]
    public int EventId { get; set; }

    [Column("channel_code"), StringLength(4)]
    public string ChannelCode { get; set; } = string.Empty;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
