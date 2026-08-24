namespace CMS_CSharp.Models;

[Table("Events")]
public class Event
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("name"), StringLength(255)]
    public string Name { get; set; } = string.Empty;

    [Column("terms_url"), StringLength(255)]
    public string TermsUrl { get; set; } = string.Empty;

    [Column("banner_url"), StringLength(255)]
    public string BannerUrl { get; set; } = string.Empty;

    [Column("slug_url"), StringLength(255)]
    public string SlugUrl { get; set; } = string.Empty;

    [Column("requires_imei")]
    public bool RequiresImei { get; set; } = true;

    [Column("requires_channel")]
    public bool RequiresChannel { get; set; } = true;

    [Column("requires_delivery")]
    public bool RequiresDelivery { get; set; }

    [Column("start_date")]
    public DateTime StartDate { get; set; }

    [Column("end_date")]
    public DateTime EndDate { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
