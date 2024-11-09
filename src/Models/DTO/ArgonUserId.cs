namespace Models.DTO;

public record struct ArgonUserId(Guid id)
{
    public string ToRawIdentity() => id.ToString("N");
}