namespace Argon.Entities;

public class ClickhouseContext : DbContext
{
    public DbSet<ArgonMessage> Messages  { get; set; }
    public Document            Documents { get; set; }
    public Image               Images    { get; set; }
    public Sticker             Stickers  { get; set; }
    public Entity              Entities  { get; set; }
}