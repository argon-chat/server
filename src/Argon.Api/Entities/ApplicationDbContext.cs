using Microsoft.EntityFrameworkCore;

namespace Argon.Api.Entities;

public class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options)
{
    public virtual DbSet<Orleansmembershiptable> Orleansmembershiptables { get; set; }

    public virtual DbSet<Orleansmembershipversiontable> Orleansmembershipversiontables { get; set; }

    public virtual DbSet<Orleansquery> Orleansqueries { get; set; }

    public virtual DbSet<Orleansreminderstable> Orleansreminderstables { get; set; }

    public virtual DbSet<Orleansstorage> Orleansstorages { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Orleansmembershiptable>(entity =>
        {
            entity.HasKey(e => new { e.Deploymentid, e.Address, e.Port, e.Generation })
                .HasName("pk_membershiptable_deploymentid");

            entity.ToTable("orleansmembershiptable");

            entity.Property(e => e.Deploymentid)
                .HasMaxLength(150)
                .HasColumnName("deploymentid");
            entity.Property(e => e.Address)
                .HasMaxLength(45)
                .HasColumnName("address");
            entity.Property(e => e.Port).HasColumnName("port");
            entity.Property(e => e.Generation).HasColumnName("generation");
            entity.Property(e => e.Hostname)
                .HasMaxLength(150)
                .HasColumnName("hostname");
            entity.Property(e => e.Iamalivetime)
                .HasPrecision(3)
                .HasColumnName("iamalivetime");
            entity.Property(e => e.Proxyport).HasColumnName("proxyport");
            entity.Property(e => e.Siloname)
                .HasMaxLength(150)
                .HasColumnName("siloname");
            entity.Property(e => e.Starttime)
                .HasPrecision(3)
                .HasColumnName("starttime");
            entity.Property(e => e.Status).HasColumnName("status");
            entity.Property(e => e.Suspecttimes)
                .HasMaxLength(8000)
                .HasColumnName("suspecttimes");

            entity.HasOne(d => d.Deployment).WithMany(p => p.Orleansmembershiptables)
                .HasForeignKey(d => d.Deploymentid)
                .OnDelete(DeleteBehavior.ClientSetNull)
                .HasConstraintName("fk_membershiptable_membershipversiontable_deploymentid");
        });

        modelBuilder.Entity<Orleansmembershipversiontable>(entity =>
        {
            entity.HasKey(e => e.Deploymentid).HasName("pk_orleansmembershipversiontable_deploymentid");

            entity.ToTable("orleansmembershipversiontable");

            entity.Property(e => e.Deploymentid)
                .HasMaxLength(150)
                .HasColumnName("deploymentid");
            entity.Property(e => e.Timestamp)
                .HasPrecision(3)
                .HasDefaultValueSql("now()")
                .HasColumnName("timestamp");
            entity.Property(e => e.Version)
                .HasDefaultValue(0)
                .HasColumnName("version");
        });

        modelBuilder.Entity<Orleansquery>(entity =>
        {
            entity.HasKey(e => e.Querykey).HasName("orleansquery_key");

            entity.ToTable("orleansquery");

            entity.Property(e => e.Querykey)
                .HasMaxLength(64)
                .HasColumnName("querykey");
            entity.Property(e => e.Querytext)
                .HasMaxLength(8000)
                .HasColumnName("querytext");
        });

        modelBuilder.Entity<Orleansreminderstable>(entity =>
        {
            entity.HasKey(e => new { e.Serviceid, e.Grainid, e.Remindername })
                .HasName("pk_reminderstable_serviceid_grainid_remindername");

            entity.ToTable("orleansreminderstable");

            entity.Property(e => e.Serviceid)
                .HasMaxLength(150)
                .HasColumnName("serviceid");
            entity.Property(e => e.Grainid)
                .HasMaxLength(150)
                .HasColumnName("grainid");
            entity.Property(e => e.Remindername)
                .HasMaxLength(150)
                .HasColumnName("remindername");
            entity.Property(e => e.Grainhash).HasColumnName("grainhash");
            entity.Property(e => e.Period).HasColumnName("period");
            entity.Property(e => e.Starttime)
                .HasPrecision(3)
                .HasColumnName("starttime");
            entity.Property(e => e.Version).HasColumnName("version");
        });

        modelBuilder.Entity<Orleansstorage>(entity =>
        {
            entity
                .HasNoKey()
                .ToTable("orleansstorage");

            entity.HasIndex(e => new { e.Grainidhash, e.Graintypehash }, "ix_orleansstorage");

            entity.Property(e => e.Grainidextensionstring)
                .HasMaxLength(512)
                .HasColumnName("grainidextensionstring");
            entity.Property(e => e.Grainidhash).HasColumnName("grainidhash");
            entity.Property(e => e.Grainidn0).HasColumnName("grainidn0");
            entity.Property(e => e.Grainidn1).HasColumnName("grainidn1");
            entity.Property(e => e.Graintypehash).HasColumnName("graintypehash");
            entity.Property(e => e.Graintypestring)
                .HasMaxLength(512)
                .HasColumnName("graintypestring");
            entity.Property(e => e.Modifiedon)
                .HasColumnType("timestamp without time zone")
                .HasColumnName("modifiedon");
            entity.Property(e => e.Payloadbinary).HasColumnName("payloadbinary");
            entity.Property(e => e.Serviceid)
                .HasMaxLength(150)
                .HasColumnName("serviceid");
            entity.Property(e => e.Version).HasColumnName("version");
        });
    }
}