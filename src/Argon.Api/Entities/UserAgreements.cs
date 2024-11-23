namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;
using Contracts.Models;

[MessagePackObject(true)]
public record UserAgreements
{
    [Key]
    public Guid Id { get;                            set; } = Guid.NewGuid();
    public bool     AllowedSendOptionalEmails { get; set; }
    public bool     AgreeTOS                  { get; set; }
    public DateTime CreatedAt                 { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt                 { get; set; }  = DateTime.UtcNow;

    public virtual User User   { get; set; }
    public         Guid UserId { get; set; }
}