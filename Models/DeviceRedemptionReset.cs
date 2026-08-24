namespace CMS_CSharp.Models;

[Table("Device_Redemption_Resets")]
public class DeviceRedemptionReset
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("imei"), StringLength(15)]
    public string Imei { get; set; } = string.Empty;

    [Column("previous_claim_id"), StringLength(255)]
    public string PreviousClaimId { get; set; } = string.Empty;

    [Column("reason", TypeName = "text")]
    public string Reason { get; set; } = string.Empty;

    [Column("reset_by")]
    public int ResetBy { get; set; }

    [Column("reset_at")]
    public DateTime ResetAt { get; set; }
}
