namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// One machine, as seen by one account.
/// </summary>
/// <remarks>
/// <para>A row per (user, device) pair rather than per login: the interesting facts are "has this
/// account been here before" and "who else has", and both are properties of the pair. Repeat logins
/// move <see cref="LastSeenAt"/> and nothing else.</para>
///
/// <para><see cref="Components"/> is stored verbatim so a returning machine can be scored against
/// it. They are already digests when they arrive — the client hashes every signal before sending —
/// so this table never holds a serial number, and a leak of it does not identify anyone's hardware
/// to anybody who does not already have the machine in front of them.</para>
/// </remarks>
public record DeviceObservationEntity : ArgonEntity, IEntityTypeConfiguration<DeviceObservationEntity>
{
    public Guid UserId { get; set; }

    /// <summary>
    /// The device this account was seen on, assigned by the server when a login failed to match any
    /// machine this account had used before.
    /// </summary>
    /// <remarks>
    /// Deliberately not derived from the components: hardware changes, and an id computed from them
    /// would change with it, which would make the history this table exists for unreadable. The id
    /// is minted once and then follows the machine through upgrades by way of scoring.
    /// </remarks>
    public Guid DeviceId { get; set; }

    /// <summary>The reported signals, in the wire format — <c>1;mg:abc,su:def</c>.</summary>
    public string Components { get; set; } = string.Empty;

    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt  { get; set; }

    /// <summary>How many logins this pair has accounted for. A shared machine shows up as a big number here.</summary>
    public int Logins { get; set; }

    public void Configure(EntityTypeBuilder<DeviceObservationEntity> builder)
    {
        // The two questions asked of this table, one index each: "which machines has this account
        // used" and "which accounts have used this machine".
        builder.HasIndex(x => x.UserId);
        builder.HasIndex(x => x.DeviceId);
        builder.HasIndex(x => new { x.UserId, x.DeviceId }).IsUnique();
    }
}

/// <summary>
/// A machine barred from signing anyone in.
/// </summary>
/// <remarks>
/// Keyed by <see cref="DeviceObservationEntity.DeviceId"/>, so a ban follows the machine through the
/// hardware changes that scoring absorbs rather than being shed by swapping a disk. Separate from
/// the observations so lifting a ban does not disturb the history that justified it.
/// </remarks>
public record DeviceBanEntity : ArgonEntity, IEntityTypeConfiguration<DeviceBanEntity>
{
    public Guid DeviceId { get; set; }

    /// <summary>Who imposed it. Null for bans placed by automation rather than a person.</summary>
    public Guid? IssuedBy { get; set; }

    public string Reason { get; set; } = string.Empty;

    /// <summary>Null for a ban with no end.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    public void Configure(EntityTypeBuilder<DeviceBanEntity> builder)
        => builder.HasIndex(x => x.DeviceId).IsUnique();
}
