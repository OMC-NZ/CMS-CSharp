namespace CMS_CSharp.Models.Auth;

[Table("accounts")]
public class Account
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("email"), StringLength(255)]
    public string Email { get; set; } = string.Empty;

    [Column("pwd_hash"), StringLength(500)]
    public string PasswordHash { get; set; } = string.Empty;

    [Column("status")]
    public byte Status { get; set; }

    [Column("security_version")]
    public int SecurityVersion { get; set; }

    [Column("last_login_at")]
    public DateTime? LastLoginAt { get; set; }

    [Column("disabled_at")]
    public DateTime? DisabledAt { get; set; }

    [Column("disabled_by_account_id")]
    public int? DisabledByAccountId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
