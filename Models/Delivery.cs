namespace CMS_CSharp.Models;

[Table("Deliveries")]
public class Delivery
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("claim_id"), StringLength(255)]
    public string ClaimId { get; set; } = string.Empty;

    [Column("reference"), StringLength(255)]
    public string Reference { get; set; } = string.Empty;
}
