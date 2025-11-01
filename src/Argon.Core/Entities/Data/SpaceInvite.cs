namespace Argon.Entities;

using Features.EF;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

public record SpaceInvite : ArgonEntityWithOwnership<ulong>, IEntityTypeConfiguration<SpaceInvite>, IMapper<SpaceInvite, InviteCodeEntity>
{
    public required DateTimeOffset ExpireAt { get; set; }
    public required Guid           SpaceId  { get; set; }
    public virtual  SpaceEntity    Space    { get; set; }

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

        //builder.PlacementRegionalByRow();

        builder.WithTTL(x => x.ExpireAt, CronValue.Daily, 
            batchSize: 5000, rangeConcurrency: 4, deleteRateLimit: 52428800);
    }

    public static InviteCodeEntity Map(scoped in SpaceInvite self)
        => throw new NotImplementedException();
}

public readonly record struct InviteCode(string inviteCode);

public readonly record struct InviteCodeEntityData(InviteCode code, Guid spaceId, Guid issuerId, DateTimeOffset expireTime, long used)
{
    public const string CacheEntityKey = $"{nameof(InviteCodeEntity)}_{{0}}";

    public bool HasExpired() => DateTimeOffset.UtcNow > expireTime;

    public static bool TryParseInviteCode(string inviteCode, out ulong? inviteId)
    {
        inviteId = null;
        if (inviteCode.Length is not (9 or 12))
            return false;
        try
        {
            inviteId = EncodeToUlong(inviteCode);
            return true;
        }
        catch (Exception e)
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