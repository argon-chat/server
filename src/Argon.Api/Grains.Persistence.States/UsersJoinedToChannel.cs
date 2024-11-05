namespace Argon.Api.Grains.Persistence.States;

using System.Runtime.Serialization;
using Entities;
using MemoryPack;
using MessagePack;

[DataContract]
[MemoryPackable(GenerateType.VersionTolerant)]
[MessagePackObject]
[Serializable]
[GenerateSerializer]
public sealed record UsersJoinedToChannel
{
    public List<UsersToServerRelation> Users { get; set; } = new();
}