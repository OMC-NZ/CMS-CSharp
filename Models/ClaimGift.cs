namespace CMS_CSharp.Models;

[Table("Claim_Gifts")]
public class ClaimGift
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("gift_id")]
    public int GiftId { get; set; }

    [Column("claim_id"), StringLength(255)]
    public string ClaimId { get; set; } = string.Empty;
}
