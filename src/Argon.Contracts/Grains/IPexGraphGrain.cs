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

[MessagePackObject(true)]
public record PexGraphScope : IArchetypeObject
{
    public Guid              ObjectId { get; set; }
    public Guid              ServerId { get; set; }
    public PexGraphScopeKind Kind     { get; set; }

    public static PexGraphScope ForChannel(Channel channel)
        => new()
        {
            Kind     = PexGraphScopeKind.Channel,
            ObjectId = channel.Id,
            ServerId = channel.ServerId,
            Overwrites = channel.Overwrites
        };

    public ICollection<IArchetypeOverwrite> Overwrites { get; private init; }
}