namespace CMS_CSharp.Models;

[Table("Deliver_Addresses")]
public class DeliveryAddress
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("claim_id"), StringLength(255)]
    public string? ClaimId { get; set; }

    [Column("street"), StringLength(255)]
    public string Street { get; set; } = string.Empty;

    [Column("suburb"), StringLength(255)]
    public string Suburb { get; set; } = string.Empty;

    [Column("city"), StringLength(255)]
    public string City { get; set; } = string.Empty;

    [Column("postcode"), StringLength(255)]
    public string Postcode { get; set; } = string.Empty;

    [Column("instructions"), StringLength(255)]
    public string? Instructions { get; set; }

    [Column("is_current")]
    public bool IsCurrent { get; set; }

    [Column("event_claim_id"), StringLength(255)]
    public string? EventClaimId { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
