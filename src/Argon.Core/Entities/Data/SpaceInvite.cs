namespace Argon.Entities;

using Features.EF;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record SpaceInvite : ArgonEntityWithOwnership<ulong>, IEntityTypeConfiguration<SpaceInvite>, IMapper<SpaceInvite, InviteCodeEntity>
{
    public required DateTimeOffset ExpireAt { get; set; }
    public required Guid           SpaceId  { get; set; }
    public virtual  SpaceEntity    Space    { get; set; }

    /// <summary>Maximum number of joins allowed through this invite. 0 = unlimited.</summary>
    public int  MaxUses   { get; set; }
    /// <summary>How many members have joined through this invite so far.</summary>
    public long UsedCount { get; set; }

    /// <summary>
    /// The voice room this invite points at, or null for a plain space invite. Kept on the same row
    /// rather than in a second table so that one code — and therefore one link — covers both "not a
    /// member yet" and "already a member, wrong room"; splitting them would force the client to try
    /// two lookups for every pasted link.
    /// </summary>
    public Guid? ChannelId { get; set; }

    public void Configure(EntityTypeBuilder<SpaceInvite> builder)
    {
        builder.HasOne(c => c.Space)
           .WithMany(s => s.ServerInvites)
           .HasForeignKey(c => c.SpaceId);


        builder.Property(x => x.Id).ValueGeneratedNever();
        builder.HasKey(x => x.Id);

        builder.Property(x => x.ExpireAt)
           .HasColumnType("TIMESTAMPTZ")
           .IsRequired();

        // Placement is declared once, in ArgonTablePlacement, and it is REGIONAL there — not global,
        // which is what this note used to claim. The claim was that an invite link has to resolve
        // from whichever region the person following it landed in; that is a read, and reads are the
        // cheap side to fix. What decided it is the write: UsedCount is incremented per accepted
        // invite by a guarded conditional update (InviteGrain, WHERE MaxUses = 0 OR UsedCount <
        // MaxUses), a compare-and-swap that is only correct against one authoritative copy — so a
        // globally replicated invite table would have paid a commit-wait on the join path for a row
        // that cannot be replicated anyway. Do not add a placement call here: two declarations is
        // how a table ends up with a locality nobody chose.

        builder.WithTTL(x => x.ExpireAt, CronValue.Daily, 
            batchSize: 5000, rangeConcurrency: 4, deleteRateLimit: 52428800);
    }

    public static InviteCodeEntity Map(scoped in SpaceInvite self)
        => throw new NotImplementedException();
}

public readonly record struct InviteCode(string inviteCode);

public readonly record struct InviteCodeEntityData(InviteCode code, Guid spaceId, Guid issuerId, DateTimeOffset expireTime, long used, int maxUses, DateTimeOffset createdAt, Guid? channelId = null)
{
    public const string CacheEntityKey = $"{nameof(InviteCodeEntity)}_{{0}}";

    public bool HasExpired() => DateTimeOffset.UtcNow > expireTime;

    public static bool TryParseInviteCode(string inviteCode, out ulong? inviteId)
    {
        inviteId = null;
        if (string.IsNullOrWhiteSpace(inviteCode))
            return false;

        // Invite codes are shared in their dashed display form ("ABC-DEF-GHI", 11 chars) produced
        // by DecodeFromUlong/GetInviteCodes. Strip separators before validating, otherwise the raw
        // length check rejects every shared code and previews/joins resolve to NOT_FOUND.
        var clean = RemoveSeparators(inviteCode);
        if (clean.Length != 9)
            return false;
        try
        {
            inviteId = EncodeToUlong(clean);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }


    public unsafe static string GenerateInviteCode(int length = 9)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        const int    Base  = 62;
        Span<byte>   bytes = stackalloc byte[length];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(bytes);

        var result = stackalloc char[length];
        for (var i = 0; i < length; i++)
            result[i] = chars[bytes[i] % chars.Length];

        return new string(result);
    }

    private static string FormatWithSeparators(string code, int every, char separator)
    {
        var        extra     = (code.Length - 1) / every;
        Span<char> formatted = stackalloc char[code.Length + extra];

        var j = 0;
        for (var i = 0; i < code.Length; i++)
        {
            if (i > 0 && i % every == 0)
                formatted[j++] = separator;

            formatted[j++] = code[i];
        }

        return new string(formatted);
    }

    public static string RemoveSeparators(string inviteCode, char separator = '-')
        => inviteCode.Replace(separator.ToString(), "");

    public static ulong EncodeToUlong(string inviteCode)
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        const int    Base  = 62;

        var   cleanCode = RemoveSeparators(inviteCode);
        ulong result    = 0;
        foreach (var c in cleanCode)
        {
            var index = chars.IndexOf(c);
            if (index == -1)
                throw new ArgumentException($"Invalid character '{c}' in invite code.");

            result = (result * (ulong)Base) + (ulong)index;
        }

        return result;
    }

    public static string DecodeFromUlong(ulong number, int length = 9, int separatorEvery = 3, char separator = '-')
    {
        const string chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
        const int    Base  = 62;

        Span<char> buffer = stackalloc char[length];
        for (var i = length - 1; i >= 0; i--)
        {
            buffer[i] =  chars[(int)(number % Base)];
            number    /= Base;
        }

        return FormatWithSeparators(new string(buffer), separatorEvery, separator);
    }
}