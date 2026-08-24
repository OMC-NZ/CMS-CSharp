namespace CMS_CSharp.Models;

[Table("Promotion_Devices")]
public class PromotionDevice
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("promotion_id")]
    public int PromotionId { get; set; }

    [Column("eligible_model"), StringLength(7)]
    public string EligibleModel { get; set; } = string.Empty;
}
