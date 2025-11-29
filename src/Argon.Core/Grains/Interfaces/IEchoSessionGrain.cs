namespace Argon.Core.Grains.Interfaces;

public record EchoJoinRequest(
    string RoomId,
    string Token,
    string VoiceFileName,
    string rtsEndpoint,
    Guid TargetUserId
);

[Alias(nameof(IEchoSessionGrain))]
public interface IEchoSessionGrain : IGrainWithGuidKey
{
    Task RequestJoinEchoAsync(EchoJoinRequest request, CancellationToken ct = default);
    Task HangupAsync(CancellationToken ct = default);
}