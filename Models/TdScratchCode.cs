namespace CMS_CSharp.Models;

[Table("TD_Scratch_Codes")]
public class TdScratchCode
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("td_code"), StringLength(255)]
    public string TdCode { get; set; } = string.Empty;

    [Column("event_claim_id"), StringLength(255)]
    public string? EventClaimId { get; set; }

    [Column("used")]
    public bool Used { get; set; }
}
