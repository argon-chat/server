namespace Argon.Entities;

using System.ComponentModel.DataAnnotations;

[MessagePackObject(true)]
public record UserAgreements
{
    [Key]
    public Guid Id { get;                                  set; } = Guid.NewGuid();
    public bool           AllowedSendOptionalEmails { get; set; }
    public bool           AgreeTOS                  { get; set; }
    public DateTimeOffset CreatedAt                 { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt                 { get; set; }  = DateTimeOffset.UtcNow;

    public virtual User User   { get; set; }
    public         Guid UserId { get; set; }
}