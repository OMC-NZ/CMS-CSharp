namespace CMS_CSharp.Models;

[Table("Event_Models")]
public class EventModel
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("event_id")]
    public int EventId { get; set; }

    [Column("eligible_model"), StringLength(45)]
    public string EligibleModel { get; set; } = string.Empty;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
