namespace CMS_CSharp.Models.Auth;

[Table("roles")]
public class Role
{
    [Key, Column("id")]
    public int Id { get; set; }

    [Column("code"), StringLength(255)]
    public string Code { get; set; } = string.Empty;

    [Column("name"), StringLength(255)]
    public string Name { get; set; } = string.Empty;

    [Column("description")]
    public string? Description { get; set; }

    [Column("status")]
    public byte Status { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
