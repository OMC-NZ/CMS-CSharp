namespace CMS_CSharp.Models;

[Table("Event_Form_Field_Options")]
public class EventFormFieldOption
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("field_id")]
    public int FieldId { get; set; }

    [Column("option_value"), StringLength(255)]
    public string OptionValue { get; set; } = string.Empty;

    [Column("option_label"), StringLength(255)]
    public string OptionLabel { get; set; } = string.Empty;

    [Column("sort_order")]
    public int SortOrder { get; set; }
}
