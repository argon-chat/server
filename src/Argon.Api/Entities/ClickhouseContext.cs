namespace Argon.Entities;

public class ClickhouseContext(DbContextOptions<ClickhouseContext> options) : DbContext(options)
{
    public DbSet<ArgonMessage> Messages  { get; set; }
    public DbSet<Document>     Documents { get; set; }
    public DbSet<Image>        Images    { get; set; }
    public DbSet<Sticker>      Stickers  { get; set; }
    public DbSet<Entity>       Entities  { get; set; }
}