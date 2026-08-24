namespace CMS_CSharp.Models;

[Table("Channels")]
public class Channel
{
    [Key, Column("code"), StringLength(4)]
    public string Code { get; set; } = string.Empty;

    [Column("name", TypeName = "text")]
    public string Name { get; set; } = string.Empty;

    [Column("category"), StringLength(45)]
    public string Category { get; set; } = string.Empty;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
