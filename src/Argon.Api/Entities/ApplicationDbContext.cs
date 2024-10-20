using Microsoft.EntityFrameworkCore;

namespace Argon.Api.Entities;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options);