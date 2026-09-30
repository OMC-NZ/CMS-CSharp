namespace CMS_CSharp.Models.Auth;

[Table("account_roles")]
public class AccountRole
{
    [Key, Column("account_id", Order = 0)]
    public int AccountId { get; set; }

    [Key, Column("role_id", Order = 1)]
    public int RoleId { get; set; }

    [Column("assigned_by_account_id")]
    public int? AssignedByAccountId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }
}
