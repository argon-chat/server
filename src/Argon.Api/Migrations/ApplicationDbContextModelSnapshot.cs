﻿// <auto-generated />
using System;
using Argon.Api.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Argon.Api.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    partial class ApplicationDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder
                .HasAnnotation("ProductVersion", "8.0.10")
                .HasAnnotation("Relational:MaxIdentifierLength", 63);

            NpgsqlModelBuilderExtensions.UseIdentityByDefaultColumns(modelBuilder);

            modelBuilder.Entity("Argon.Api.Entities.UserAgreements", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<bool>("AgreeTOS")
                        .HasColumnType("boolean");

                    b.Property<bool>("AllowedSendOptionalEmails")
                        .HasColumnType("boolean");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("UserId")
                        .HasColumnType("uuid");

                    b.HasKey("Id");

                    b.HasIndex("UserId");

                    b.ToTable("UserAgreements");
                });

            modelBuilder.Entity("Argon.Contracts.Models.ArchetypeModel.Archetype", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<int>("Colour")
                        .HasColumnType("integer");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("CreatorId")
                        .HasColumnType("uuid");

                    b.Property<DateTime?>("DeletedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Description")
                        .IsRequired()
                        .HasMaxLength(512)
                        .HasColumnType("character varying(512)");

                    b.Property<decimal>("Entitlement")
                        .HasColumnType("numeric(20,0)");

                    b.Property<string>("IconFileId")
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)");

                    b.Property<bool>("IsDeleted")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsHidden")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsLocked")
                        .HasColumnType("boolean");

                    b.Property<bool>("IsMentionable")
                        .HasColumnType("boolean");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(64)
                        .HasColumnType("character varying(64)");

                    b.Property<Guid>("ServerId")
                        .HasColumnType("uuid");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.HasIndex("CreatorId");

                    b.HasIndex("ServerId");

                    b.ToTable("Archetypes");

                    b.HasData(
                        new
                        {
                            Id = new Guid("11111111-3333-0000-1111-111111111111"),
                            Colour = -8355712,
                            CreatedAt = new DateTime(2024, 11, 23, 16, 1, 14, 205, DateTimeKind.Utc).AddTicks(8382),
                            CreatorId = new Guid("11111111-2222-1111-2222-111111111111"),
                            Description = "Default role for everyone in this server",
                            Entitlement = 15760355m,
                            IsDeleted = false,
                            IsHidden = false,
                            IsLocked = false,
                            IsMentionable = true,
                            Name = "everyone",
                            ServerId = new Guid("11111111-0000-1111-1111-111111111111"),
                            UpdatedAt = new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified)
                        },
                        new
                        {
                            Id = new Guid("11111111-4444-0000-1111-111111111111"),
                            Colour = -8355712,
                            CreatedAt = new DateTime(2024, 11, 23, 16, 1, 14, 205, DateTimeKind.Utc).AddTicks(8411),
                            CreatorId = new Guid("11111111-2222-1111-2222-111111111111"),
                            Description = "Default role for owner in this server",
                            Entitlement = -1m,
                            IsDeleted = false,
                            IsHidden = true,
                            IsLocked = true,
                            IsMentionable = false,
                            Name = "owner",
                            ServerId = new Guid("11111111-0000-1111-1111-111111111111"),
                            UpdatedAt = new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified)
                        });
                });

            modelBuilder.Entity("Argon.Contracts.Models.Channel", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<int>("ChannelType")
                        .HasColumnType("integer");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("CreatorId")
                        .HasColumnType("uuid");

                    b.Property<DateTime?>("DeletedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Description")
                        .HasMaxLength(1024)
                        .HasColumnType("character varying(1024)");

                    b.Property<bool>("IsDeleted")
                        .HasColumnType("boolean");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)");

                    b.Property<Guid>("ServerId")
                        .HasColumnType("uuid");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.HasIndex("CreatorId");

                    b.HasIndex("ServerId");

                    b.HasIndex("Id", "ServerId");

                    b.ToTable("Channels");
                });

            modelBuilder.Entity("Argon.Contracts.Models.ChannelEntitlementOverwrite", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<decimal>("Allow")
                        .HasColumnType("numeric(20,0)");

                    b.Property<Guid?>("ArchetypeId")
                        .HasColumnType("uuid");

                    b.Property<Guid>("ChannelId")
                        .HasColumnType("uuid");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("CreatorId")
                        .HasColumnType("uuid");

                    b.Property<DateTime?>("DeletedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<decimal>("Deny")
                        .HasColumnType("numeric(20,0)");

                    b.Property<bool>("IsDeleted")
                        .HasColumnType("boolean");

                    b.Property<int>("Scope")
                        .HasColumnType("integer");

                    b.Property<Guid?>("ServerMemberId")
                        .HasColumnType("uuid");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.HasIndex("ArchetypeId");

                    b.HasIndex("ChannelId");

                    b.HasIndex("CreatorId");

                    b.HasIndex("ServerMemberId");

                    b.ToTable("ChannelEntitlementOverwrites");
                });

            modelBuilder.Entity("Argon.Contracts.Models.Server", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<string>("AvatarFileId")
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("CreatorId")
                        .HasColumnType("uuid");

                    b.Property<DateTime?>("DeletedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Description")
                        .HasMaxLength(1024)
                        .HasColumnType("character varying(1024)");

                    b.Property<bool>("IsDeleted")
                        .HasColumnType("boolean");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(64)
                        .HasColumnType("character varying(64)");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.HasKey("Id");

                    b.HasIndex("CreatorId");

                    b.ToTable("Servers");

                    b.HasData(
                        new
                        {
                            Id = new Guid("11111111-0000-1111-1111-111111111111"),
                            AvatarFileId = "",
                            CreatedAt = new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                            CreatorId = new Guid("11111111-2222-1111-2222-111111111111"),
                            Description = "",
                            IsDeleted = false,
                            Name = "system_server",
                            UpdatedAt = new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified)
                        });
                });

            modelBuilder.Entity("Argon.Contracts.Models.ServerMember", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("CreatorId")
                        .HasColumnType("uuid");

                    b.Property<DateTime?>("DeletedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<bool>("IsDeleted")
                        .HasColumnType("boolean");

                    b.Property<DateTime>("JoinedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("ServerId")
                        .HasColumnType("uuid");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<Guid>("UserId")
                        .HasColumnType("uuid");

                    b.HasKey("Id");

                    b.HasIndex("CreatorId");

                    b.HasIndex("ServerId");

                    b.HasIndex("UserId");

                    b.ToTable("UsersToServerRelations");
                });

            modelBuilder.Entity("Argon.Contracts.Models.ServerMemberArchetype", b =>
                {
                    b.Property<Guid>("ServerMemberId")
                        .HasColumnType("uuid");

                    b.Property<Guid>("ArchetypeId")
                        .HasColumnType("uuid");

                    b.HasKey("ServerMemberId", "ArchetypeId");

                    b.HasIndex("ArchetypeId");

                    b.ToTable("ServerMemberArchetypes");
                });

            modelBuilder.Entity("Argon.Contracts.Models.User", b =>
                {
                    b.Property<Guid>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("uuid");

                    b.Property<string>("AvatarFileId")
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<DateTime?>("DeletedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("DisplayName")
                        .IsRequired()
                        .HasMaxLength(64)
                        .HasColumnType("character varying(64)");

                    b.Property<string>("Email")
                        .IsRequired()
                        .HasMaxLength(255)
                        .HasColumnType("character varying(255)");

                    b.Property<bool>("IsDeleted")
                        .HasColumnType("boolean");

                    b.Property<string>("OtpHash")
                        .HasMaxLength(128)
                        .HasColumnType("character varying(128)");

                    b.Property<string>("PasswordDigest")
                        .HasMaxLength(512)
                        .HasColumnType("character varying(512)");

                    b.Property<string>("PhoneNumber")
                        .HasMaxLength(64)
                        .HasColumnType("character varying(64)");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("timestamp with time zone");

                    b.Property<string>("Username")
                        .IsRequired()
                        .HasMaxLength(64)
                        .HasColumnType("character varying(64)");

                    b.HasKey("Id");

                    b.ToTable("Users");

                    b.HasData(
                        new
                        {
                            Id = new Guid("11111111-2222-1111-2222-111111111111"),
                            CreatedAt = new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                            DisplayName = "System",
                            Email = "system@argon.gl",
                            IsDeleted = false,
                            UpdatedAt = new DateTime(1, 1, 1, 0, 0, 0, 0, DateTimeKind.Unspecified),
                            Username = "system"
                        });
                });

            modelBuilder.Entity("Argon.Api.Entities.UserAgreements", b =>
                {
                    b.HasOne("Argon.Contracts.Models.User", "User")
                        .WithMany()
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("User");
                });

            modelBuilder.Entity("Argon.Contracts.Models.ArchetypeModel.Archetype", b =>
                {
                    b.HasOne("Argon.Contracts.Models.Server", "Server")
                        .WithMany("Archetypes")
                        .HasForeignKey("ServerId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Server");
                });

            modelBuilder.Entity("Argon.Contracts.Models.Channel", b =>
                {
                    b.HasOne("Argon.Contracts.Models.Server", "Server")
                        .WithMany("Channels")
                        .HasForeignKey("ServerId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Server");
                });

            modelBuilder.Entity("Argon.Contracts.Models.ChannelEntitlementOverwrite", b =>
                {
                    b.HasOne("Argon.Contracts.Models.ArchetypeModel.Archetype", "Archetype")
                        .WithMany()
                        .HasForeignKey("ArchetypeId")
                        .OnDelete(DeleteBehavior.Restrict);

                    b.HasOne("Argon.Contracts.Models.Channel", "Channel")
                        .WithMany("EntitlementOverwrites")
                        .HasForeignKey("ChannelId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("Argon.Contracts.Models.ServerMember", "ServerMember")
                        .WithMany()
                        .HasForeignKey("ServerMemberId")
                        .OnDelete(DeleteBehavior.Restrict);

                    b.Navigation("Archetype");

                    b.Navigation("Channel");

                    b.Navigation("ServerMember");
                });

            modelBuilder.Entity("Argon.Contracts.Models.ServerMember", b =>
                {
                    b.HasOne("Argon.Contracts.Models.Server", "Server")
                        .WithMany("Users")
                        .HasForeignKey("ServerId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("Argon.Contracts.Models.User", "User")
                        .WithMany("ServerMembers")
                        .HasForeignKey("UserId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Server");

                    b.Navigation("User");
                });

            modelBuilder.Entity("Argon.Contracts.Models.ServerMemberArchetype", b =>
                {
                    b.HasOne("Argon.Contracts.Models.ArchetypeModel.Archetype", "Archetype")
                        .WithMany("ServerMemberRoles")
                        .HasForeignKey("ArchetypeId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.HasOne("Argon.Contracts.Models.ServerMember", "ServerMember")
                        .WithMany("ServerMemberArchetypes")
                        .HasForeignKey("ServerMemberId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Archetype");

                    b.Navigation("ServerMember");
                });

            modelBuilder.Entity("Argon.Contracts.Models.ArchetypeModel.Archetype", b =>
                {
                    b.Navigation("ServerMemberRoles");
                });

            modelBuilder.Entity("Argon.Contracts.Models.Channel", b =>
                {
                    b.Navigation("EntitlementOverwrites");
                });

            modelBuilder.Entity("Argon.Contracts.Models.Server", b =>
                {
                    b.Navigation("Archetypes");

                    b.Navigation("Channels");

                    b.Navigation("Users");
                });

            modelBuilder.Entity("Argon.Contracts.Models.ServerMember", b =>
                {
                    b.Navigation("ServerMemberArchetypes");
                });

            modelBuilder.Entity("Argon.Contracts.Models.User", b =>
                {
                    b.Navigation("ServerMembers");
                });
#pragma warning restore 612, 618
        }
    }
}
