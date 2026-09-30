namespace CMS_CSharp.Models.Auth;

[Table("permissions")]
public class Permission
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("code"), StringLength(255)]
    public string Code { get; set; } = string.Empty;

    [Column("module"), StringLength(255)]
    public string Module { get; set; } = string.Empty;

    [Column("action"), StringLength(255)]
    public string Action { get; set; } = string.Empty;

    [Column("status")]
    public byte Status { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
