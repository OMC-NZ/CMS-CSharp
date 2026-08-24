namespace CMS_CSharp.Models;

[Table("Event_Form_Custom_Fields")]
public class EventFormCustomField
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("section_id")]
    public int SectionId { get; set; }

    [Column("field_key"), StringLength(100)]
    public string FieldKey { get; set; } = string.Empty;

    [Column("field_label"), StringLength(255)]
    public string FieldLabel { get; set; } = string.Empty;

    [Column("field_type"), StringLength(50)]
    public string FieldType { get; set; } = string.Empty;

    [Column("placeholder"), StringLength(255)]
    public string? Placeholder { get; set; }

    [Column("is_required")]
    public bool IsRequired { get; set; }

    [Column("sort_order")]
    public int SortOrder { get; set; }

    [Column("validation_json", TypeName = "longtext")]
    public string? ValidationJson { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
