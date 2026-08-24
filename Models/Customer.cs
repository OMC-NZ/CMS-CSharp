namespace CMS_CSharp.Models;

[Table("Customers")]
public class Customer
{
    [Key, Column("id"), DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    [Column("first_name"), StringLength(45)]
    public string FirstName { get; set; } = string.Empty;

    [Column("last_name"), StringLength(45)]
    public string LastName { get; set; } = string.Empty;

    [Column("email"), StringLength(100)]
    public string Email { get; set; } = string.Empty;

    [Column("contact"), StringLength(45)]
    public string Contact { get; set; } = string.Empty;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; }
}
