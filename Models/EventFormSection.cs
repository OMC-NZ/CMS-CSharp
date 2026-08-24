namespace CMS_CSharp.Models;

[Table("Event_Form_Sections")]
public class EventFormSection
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("event_id")]
    public int EventId { get; set; }

    [Column("section_title"), StringLength(255)]
    public string? SectionTitle { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
