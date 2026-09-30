namespace CMS_CSharp.Models.Auth;

[Table("sessions")]
public class AuthSession
{
    [Key, Column("id"), StringLength(36)]
    public string Id { get; set; } = string.Empty;

    [Column("account_id")]
    public int AccountId { get; set; }

    [Column("refresh_token_hash"), StringLength(64)]
    public string RefreshTokenHash { get; set; } = string.Empty;

    [Column("security_version")]
    public int SecurityVersion { get; set; }

    [Column("ip_address"), StringLength(45)]
    public string? IpAddress { get; set; }

    [Column("user_agent"), StringLength(500)]
    public string? UserAgent { get; set; }

    [Column("expires_at")]
    public DateTime ExpiresAt { get; set; }

    [Column("revoked_at")]
    public DateTime? RevokedAt { get; set; }

    [Column("revoked_by_account_id")]
    public int? RevokedByAccountId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
