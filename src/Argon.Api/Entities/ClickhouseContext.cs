namespace Argon.Entities;

public class ClickhouseContext(DbContextOptions<ClickhouseContext> options) : DbContext(options)
{
    public DbSet<ArgonMessage>    Messages  { get; set; }
    public DbSet<MessageDocument> Documents { get; set; }
    public DbSet<MessageImage>    Images    { get; set; }
    public DbSet<Sticker>         Stickers  { get; set; }
    public DbSet<MessageEntity>   Entities  { get; set; }
}