namespace CMS_CSharp.Models;

[Table("Promotions")]
public class Promotion
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("name"), StringLength(255)]
    public string Name { get; set; } = string.Empty;

    [Column("description", TypeName = "text")]
    public string Description { get; set; } = string.Empty;

    [Column("banner_url"), StringLength(255)]
    public string BannerUrl { get; set; } = string.Empty;

    [Column("slug_url"), StringLength(255)]
    public string SlugUrl { get; set; } = string.Empty;

    [Column("terms_url"), StringLength(255)]
    public string TermsUrl { get; set; } = string.Empty;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
