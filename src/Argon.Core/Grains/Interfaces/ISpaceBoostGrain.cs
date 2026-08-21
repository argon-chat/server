namespace Argon.Grains.Interfaces;

[Alias(nameof(ISpaceBoostGrain))]
public interface ISpaceBoostGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetBoostStatusAsync))]
    Task<SpaceBoostStatus> GetBoostStatusAsync(CancellationToken ct = default);

    [Alias(nameof(RecalculateAsync))]
    Task RecalculateAsync(CancellationToken ct = default);

    [Alias(nameof(AddBoostAsync))]
    Task AddBoostAsync(Guid userId, Guid boostEntityId, CancellationToken ct = default);

    [Alias(nameof(RemoveBoostAsync))]
    Task RemoveBoostAsync(Guid boostEntityId, CancellationToken ct = default);
}
