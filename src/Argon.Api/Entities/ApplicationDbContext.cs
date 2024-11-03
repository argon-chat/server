namespace Argon.Api.Entities;

using Microsoft.EntityFrameworkCore;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<User> Users { get; }
    public DbSet<Server> Servers { get; }
    public DbSet<Channel> Channels { get; }
    public DbSet<UsersToServerRelation> UsersToServerRelations { get; }
}