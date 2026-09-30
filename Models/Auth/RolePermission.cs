namespace CMS_CSharp.Models.Auth;

[Table("role_permissions")]
public class RolePermission
{
    [Key, Column("role_id", Order = 0)]
    public int RoleId { get; set; }

    [Key, Column("permission_id", Order = 1)]
    public int PermissionId { get; set; }

    [Column("assigned_by_account_id")]
    public int? AssignedByAccountId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
