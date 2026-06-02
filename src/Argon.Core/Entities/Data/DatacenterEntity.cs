namespace Argon.Entities;

using Argon.Features.EF;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

/// <summary>
/// Represents a registered datacenter in the system.
/// Auto-registered by each DC on startup, heartbeated periodically.
/// Stored in CockroachDB with GLOBAL placement (replicated to all regions).
/// </summary>
public record DatacenterEntity : ArgonEntity<string>, IEntityTypeConfiguration<DatacenterEntity>
{
    /// <summary>Human-readable display name (e.g. "Europe West", "US East")</summary>
    public required string DisplayName { get; set; }

    /// <summary>CockroachDB region name for locality (e.g. "eu-west-1", "ru-central")</summary>
    public required string CockroachRegion { get; set; }

    /// <summary>NATS gateway URL for cross-DC communication</summary>
    public string? NatsGatewayUrl { get; set; }

    /// <summary>Public API endpoint (load balancer URL)</summary>
    public string? PublicEndpoint { get; set; }

    /// <summary>Current operational status</summary>
    public DatacenterStatus Status { get; set; } = DatacenterStatus.Online;

    /// <summary>Last heartbeat timestamp from this DC</summary>
    public DateTimeOffset LastHeartbeatAt { get; set; }

    /// <summary>Number of active user connections on this DC</summary>
    public int ActiveConnections { get; set; }

    /// <summary>Structured metadata (version, capabilities, etc.)</summary>
    public string? Metadata { get; set; }

    public void Configure(EntityTypeBuilder<DatacenterEntity> builder)
    {
        builder.ToTable("datacenters");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasMaxLength(64);
        builder.Property(x => x.DisplayName).HasMaxLength(128);
        builder.Property(x => x.CockroachRegion).HasMaxLength(128);
        builder.Property(x => x.NatsGatewayUrl).HasMaxLength(512);
        builder.Property(x => x.PublicEndpoint).HasMaxLength(512);
        builder.Property(x => x.Metadata).HasColumnType("jsonb");
        builder.PlacementGlobal();
    }
}

public enum DatacenterStatus
{
    /// <summary>DC is fully operational</summary>
    Online = 0,

    /// <summary>DC is draining connections (maintenance)</summary>
    Draining = 1,

    /// <summary>DC is offline / unreachable (detected by missed heartbeats)</summary>
    Offline = 2,

    /// <summary>DC is starting up, not yet ready to serve traffic</summary>
    Starting = 3
}
