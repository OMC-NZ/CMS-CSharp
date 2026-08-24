namespace CMS_CSharp.Models;

[Table("Claims")]
public class Claim
{
    [Key, Column("id"), StringLength(255)]
    public string Id { get; set; } = string.Empty;

    [Column("promotion_id")]
    public int PromotionId { get; set; }

    [Column("imei"), StringLength(15)]
    public string Imei { get; set; } = string.Empty;

    [Column("customer_id")]
    public int CustomerId { get; set; }

    [Column("purchase_date")]
    public DateTime PurchaseDate { get; set; }

    [Column("status")]
    public sbyte Status { get; set; }

    [Column("receipt_url"), StringLength(255)]
    public string ReceiptUrl { get; set; } = string.Empty;

    [Column("screenshot_url"), StringLength(255)]
    public string ScreenshotUrl { get; set; } = string.Empty;

    [Column("email_status")]
    public bool EmailStatus { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
