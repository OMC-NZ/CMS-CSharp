namespace CMS_CSharp.Models;

[Table("Gifts")]
public class Gift
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("name"), StringLength(45)]
    public string Name { get; set; } = string.Empty;

    [Column("alias"), StringLength(45)]
    public string Alias { get; set; } = string.Empty;

    [Column("color"), StringLength(45)]
    public string Color { get; set; } = string.Empty;

    [Column("status"), Range(0, 9)]
    public sbyte Status { get; set; }

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
