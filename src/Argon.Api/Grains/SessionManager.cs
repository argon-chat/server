namespace Argon.Api.Grains;

using Entities;
using Interfaces;
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
    public async Task<UserDto> GetUser() => await grainFactory.GetGrain<IUserGrain>(this.GetPrimaryKey()).GetUser();

    public Task Logout() => throw new NotImplementedException();
}