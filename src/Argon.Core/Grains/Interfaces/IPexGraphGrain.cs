namespace Argon.Grains.Interfaces;

using ArchetypeModel;

public interface IPexGraphGrain
{
    Task<bool> HasAccessTo(Guid userId, PexGraphScope obj, ArgonEntitlement targetCheck);
}

public enum PexGraphScopeKind
{
    Server,
    Channel
}


public record PexGraphScope : IArchetypeObject
{
    public Guid              ObjectId { get; set; }
    public Guid              SpaceId { get; set; }
    public PexGraphScopeKind Kind     { get; set; }

    public static PexGraphScope ForChannel(ChannelEntity channel)
        => new()
        {
            Kind     = PexGraphScopeKind.Channel,
            ObjectId = channel.Id,
            SpaceId = channel.SpaceId,
            Overwrites = channel.Overwrites
        };

    public ICollection<IArchetypeOverwrite> Overwrites { get; private init; }
}