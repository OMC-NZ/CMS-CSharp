namespace CMS_CSharp.Models;

[Table("Promotion_Channels")]
public class PromotionChannel
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("promotion_id")]
    public int PromotionId { get; set; }

    [Column("channel_code"), StringLength(4)]
    public string ChannelCode { get; set; } = string.Empty;

    [Column("start_date")]
    public DateTime StartDate { get; set; }

    [Column("end_date")]
    public DateTime EndDate { get; set; }

    [Column("redeem_end_date")]
    public DateTime RedeemEndDate { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
