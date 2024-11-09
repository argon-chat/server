namespace Models;

using Microsoft.EntityFrameworkCore;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public DbSet<User>                  Users                  { get; set; }
    public DbSet<Server>                Servers                { get; set; }
    public DbSet<Channel>               Channels               { get; set; }
    public DbSet<UsersToServerRelation> UsersToServerRelations { get; set; }
}