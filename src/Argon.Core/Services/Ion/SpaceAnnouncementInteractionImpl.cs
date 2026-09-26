namespace Argon.Services.Ion;

using ArgonContracts;

public sealed class SpaceAnnouncementInteractionImpl : ISpaceAnnouncementInteraction
{
    public async Task<ISetMainAnnouncementChannelResult> SetMainAnnouncementChannel(Guid spaceId, Guid? channelId,
        CancellationToken ct = default)
    {
        var error = await this.GetGrain<ISpaceGrain>(spaceId).SetMainAnnouncementChannel(channelId);

        return error is SetMainAnnouncementChannelError.NONE
            ? new SuccessSetMainAnnouncementChannel(channelId)
            : new FailedSetMainAnnouncementChannel(error);
    }
}
