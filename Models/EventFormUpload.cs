namespace CMS_CSharp.Models;

[Table("Event_Form_Uploads")]
public class EventFormUpload
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("event_id")]
    public int EventId { get; set; }

    [Column("upload_key"), StringLength(100)]
    public string UploadKey { get; set; } = string.Empty;

    [Column("upload_label"), StringLength(255)]
    public string UploadLabel { get; set; } = string.Empty;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
