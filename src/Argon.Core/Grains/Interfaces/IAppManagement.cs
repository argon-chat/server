namespace Argon.Grains.Interfaces;

[Alias("Argon.Grains.Interfaces.IAppManagement")]
public interface IAppManagement : IGrainWithGuidKey
{
    //[Alias(nameof(CreateTeamAsync))]
    //Task<Guid> CreateTeamAsync(Guid ownerId, string name, CancellationToken ct = default);
}