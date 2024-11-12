namespace Argon.Api.Entities;

public enum ServerRole : ushort // TODO: sort out roles and how we actually want to handle them
{
    User,
    Admin,
    Owner
}

public record UsersToServerRelation
{
    public         Guid       Id        { get; init; } = Guid.NewGuid();
    public         DateTime   CreatedAt { get; init; } = DateTime.UtcNow;
    public         DateTime   UpdatedAt { get; set; }  = DateTime.UtcNow;
    public         Guid       ServerId  { get; set; }  = Guid.Empty;
    public         Server     Server    { get; set; }
    public         DateTime   Joined    { get; set; } = DateTime.UtcNow;
    public         ServerRole Role      { get; set; } = ServerRole.User;
    public         Guid       UserId    { get; set; } = Guid.Empty;
    public virtual User       User      { get; set; }
}

[DataContract, MemoryPackable(GenerateType.VersionTolerant), MessagePackObject, Serializable, GenerateSerializer,
 Alias(nameof(UsersToServerRelationDto))]
public sealed partial record UsersToServerRelationDto(
    [property: DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    DateTime Joined,
    [property: DataMember(Order = 1), MemoryPackOrder(1), Id(1)]
    ServerRole Role,
    [property: DataMember(Order = 2), MemoryPackOrder(2), Id(2)]
    Guid ServerId,
    [property: DataMember(Order = 3), MemoryPackOrder(3), Id(3)]
    UserDto User);