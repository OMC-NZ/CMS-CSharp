namespace CMS_CSharp.Models.Auth;

[Table("users")]
public class CmsUser
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("account_id")]
    public int? AccountId { get; set; }

    [Column("first_name"), StringLength(255)]
    public string FirstName { get; set; } = string.Empty;

    [Column("last_name"), StringLength(255)]
    public string LastName { get; set; } = string.Empty;

    [Column("display_name"), StringLength(255)]
    public string? DisplayName { get; set; }

    [Column("status")]
    public byte Status { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
