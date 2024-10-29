namespace Argon.Api.Entities;

public class ApplicationRecord
{
    public Guid Id { get; set; } = Guid.Empty;
    public DateTime CreatedAt { get; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}