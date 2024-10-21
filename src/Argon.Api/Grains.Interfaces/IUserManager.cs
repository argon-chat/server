// namespace Argon.Api.Grains.Interfaces;
//
// using Entities;
//
// public interface IUserManager : IGrainWithIntegerKey
// {
//     [Alias("Create")]
//     Task<ApplicationUser> Create(string username, string password);
//
//     [Alias("Get")]
//     Task<ApplicationUser> Get(Guid id);
//
//     [Alias("Authenticate")]
//     Task<string> Authenticate(string username, string password);
// }