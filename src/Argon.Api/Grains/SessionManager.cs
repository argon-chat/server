namespace Argon.Grains;

using Microsoft.Extensions.Logging;
using Orleans;
using Services;

public class SessionManager(
    IGrainFactory grainFactory,
    ILogger<UserGrain> logger,
    UserManagerService managerService,
    IPasswordHashingService passwordHashingService,
    ApplicationDbContext context) : Grain, ISessionManager
{
    public async Task<User> GetUser() => await grainFactory.GetGrain<IUserGrain>(this.GetPrimaryKey()).GetMe();

    public Task Logout() => throw new NotImplementedException();
}