namespace Argon.Api.Grains;

using Entities;
using Interfaces;
using Microsoft.Extensions.Logging;
using Orleans;
using Services;

public class SessionManager(
    IGrainFactory grainFactory,
    ILogger<UserManager> logger,
    UserManagerService managerService,
    IPasswordHashingService passwordHashingService,
    ApplicationDbContext context) : Grain, ISessionManager
{
    public async Task<UserDto> GetUser() => await grainFactory.GetGrain<IUserManager>(this.GetPrimaryKey()).GetUser();

    public Task Logout() => throw new NotImplementedException();
}