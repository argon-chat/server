namespace Argon.Api.Entities;

using System.ComponentModel.DataAnnotations;

public sealed record Server
{
    public Guid     Id        { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; }  = DateTime.UtcNow;
    [MaxLength(255)]
    public string Name { get; set; } = string.Empty;
    [MaxLength(255)]
    public string? Description { get; set; } = string.Empty;
    [MaxLength(255)]
    public string? AvatarUrl { get;                                  set; } = string.Empty;
    public List<Channel>               Channels               { get; set; } = new();
    public List<UsersToServerRelation> UsersToServerRelations { get; set; } = new();
}

[DataContract, MemoryPackable(GenerateType.CircularReference), Serializable, Alias(nameof(ServerDto))]
public partial record ServerDto
{
    [MemoryPackConstructor]
    public ServerDto() { }

    public ServerDto(Guid Id, DateTime CreatedAt, DateTime UpdatedAt, string Name, string? Description, string? AvatarUrl, List<ChannelDto> Channels,
        List<UsersToServerRelationDto> Users)
    {
        this.Id          = Id;
        this.CreatedAt   = CreatedAt;
        this.UpdatedAt   = UpdatedAt;
        this.Name        = Name;
        this.Description = Description;
        this.AvatarUrl   = AvatarUrl;
        this.Channels    = Channels;
        this.Users       = Users;
    }

    [DataMember(Order = 0), MemoryPackOrder(0), Id(0)]
    public Guid Id { get; set; }
    [DataMember(Order = 1), MemoryPackOrder(1), Id(1)]
    public DateTime CreatedAt { get; set; }
    [DataMember(Order = 2), MemoryPackOrder(2), Id(2)]
    public DateTime UpdatedAt { get; set; }
    [DataMember(Order = 3), MemoryPackOrder(3), Id(3)]
    public string Name { get; set; }
    [DataMember(Order = 4), MemoryPackOrder(4), Id(4)]
    public string? Description { get; set; }
    [DataMember(Order = 5), MemoryPackOrder(5), Id(5)]
    public string? AvatarUrl { get; set; }
    [DataMember(Order = 6), MemoryPackOrder(6), Id(6)]
    public List<ChannelDto> Channels { get; set; }
    [DataMember(Order = 7), MemoryPackOrder(7), Id(7)]
    public List<UsersToServerRelationDto> Users { get; set; }
}