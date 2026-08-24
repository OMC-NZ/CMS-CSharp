namespace CMS_CSharp.Models;

[Table("Track_Trace")]
public class TrackTrace
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("address_id")]
    public int AddressId { get; set; }

    [Column("track_link"), StringLength(255)]
    public string TrackLink { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
