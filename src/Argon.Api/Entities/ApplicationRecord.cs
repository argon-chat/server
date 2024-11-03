namespace Argon.Api.Entities;

public class ApplicationRecord
{
    [Id(0)] public Guid Id { get; set; } = Guid.Empty;
    [Id(1)] public DateTime CreatedAt { get; private set; } = DateTime.UtcNow;
    [Id(2)] public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}