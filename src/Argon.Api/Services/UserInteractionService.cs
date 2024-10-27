namespace Argon.Api.Services;

using Contracts;

public class UserInteractionService(
    string username, // TODO to be injected
    IGrainFactory grainFactory
) : IUserInteraction
{
    public Task<UserResponse> GetMe()
    {
        throw new NotImplementedException();
    }

    public Task<ServerResponse> CreateServer(CreateServerRequest request)
    {
        throw new NotImplementedException();
    }

    public Task<List<ServerResponse>> GetServers()
    {
        throw new NotImplementedException();
    }

    public Task<List<ServerDetailsResponse>> GetServerDetails()
    {
        throw new NotImplementedException();
    }

    public Task<ChannelJoinResponse> JoinChannel(ChannelJoinRequest request)
    {
        throw new NotImplementedException();
    }
}