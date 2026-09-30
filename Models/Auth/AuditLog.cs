namespace CMS_CSharp.Models.Auth;

[Table("audit_logs")]
public class AuditLog
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Column("account_id")]
    public int? AccountId { get; set; }

    [Column("action"), StringLength(150)]
    public string Action { get; set; } = string.Empty;

    [Column("entity_type"), StringLength(100)]
    public string EntityType { get; set; } = string.Empty;

    [Column("entity_id"), StringLength(255)]
    public string? EntityId { get; set; }

    [Column("old_values")]
    public string? OldValues { get; set; }

    [Column("new_values")]
    public string? NewValues { get; set; }

    [Column("request_id"), StringLength(100)]
    public string? RequestId { get; set; }

    [Column("ip_address"), StringLength(45)]
    public string? IpAddress { get; set; }

    [Column("user_agent"), StringLength(500)]
    public string? UserAgent { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
