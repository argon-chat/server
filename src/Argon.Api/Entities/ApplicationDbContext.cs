namespace Argon.Api.Entities;

using Microsoft.EntityFrameworkCore;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    private DbSet<ApplicationUser> Users { get; set; }
}