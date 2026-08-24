namespace CMS_CSharp.Models;

[Table("Promotion_Gifts")]
public class PromotionGift
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("promotion_id")]
    public int PromotionId { get; set; }

    [Column("gift_id")]
    public int GiftId { get; set; }
}
