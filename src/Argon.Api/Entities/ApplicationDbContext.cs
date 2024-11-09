namespace Argon.Api.Entities;

using Microsoft.EntityFrameworkCore;
using Models;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : AbstractApplicationDbContext(options)
{
}