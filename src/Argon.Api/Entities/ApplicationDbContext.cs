namespace Models;

using Microsoft.EntityFrameworkCore;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : AbstractApplicationDbContext(options)
{
}