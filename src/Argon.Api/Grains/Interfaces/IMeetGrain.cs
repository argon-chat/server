namespace Argon.Grains.Interfaces;

using Shared.Servers;

[Alias("Argon.Grains.Interfaces.IMeetGrain")]
public interface IMeetGrain : IGrainWithStringKey
{
    [Alias("JoinAsync")]
    ValueTask<Either<MeetAuthorizationData, MeetJoinError>> AcceptAsync(string Name);
}
