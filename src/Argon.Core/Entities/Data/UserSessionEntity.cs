namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

// One signed-in device: a row per credential session (the refresh token's sid), not per machine and
// not per launch. Written through the Redis transit in SessionRegistryTransit and flushed by
// SessionRegistryFlushGrain; read by the devices screen.
public record UserSessionEntity : IEntityTypeConfiguration<UserSessionEntity>
{
    public Guid UserId              { get; set; }
    public Guid CredentialSessionId { get; set; }

    // The last presence sid this credential was seen with. Installed clients mint a new one per
    // launch, so this is a pointer to "online now", not an identity.
    public Guid? PresenceSessionId { get; set; }

    [MaxLength(64)]  public string MachineId  { get; set; } = "";
    [MaxLength(512)] public string ClientName { get; set; } = "";
    [MaxLength(8)]   public string Region     { get; set; } = "";
    [MaxLength(128)] public string City       { get; set; } = "";
    [MaxLength(64)]  public string Ip         { get; set; } = "";
    [MaxLength(64)]  public string AppId      { get; set; } = "";
    [MaxLength(128)] public string AppName    { get; set; } = "";
    [MaxLength(64)]  public string AppVersion { get; set; } = "";
    public ClientPlatform Platform { get; set; }
    [MaxLength(128)] public string OsName     { get; set; } = "";
    [MaxLength(128)] public string DeviceName { get; set; } = "";

    public DateTimeOffset CreatedAt  { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }

    public void Configure(EntityTypeBuilder<UserSessionEntity> builder)
    {
        builder.HasKey(x => new { x.UserId, x.CredentialSessionId });

        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.LastSeenAt);

        builder.HasOne<UserEntity>()
           .WithMany()
           .HasForeignKey(x => x.UserId)
           .OnDelete(DeleteBehavior.Cascade);
    }
}
