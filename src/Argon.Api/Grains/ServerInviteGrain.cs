namespace Argon.Grains;

using Orleans.Concurrency;
using Shared.Servers;

[StatelessWorker]
public class ServerInviteGrain(ILogger<IServerInvitesGrain> logger, IDbContextFactory<ApplicationDbContext> context) : Grain, IServerInvitesGrain
{
    public async Task<InviteCode> CreateInviteLinkAsync(Guid issuer, TimeSpan expiration)
    {
        await using var db         = await context.CreateDbContextAsync();
        var             inviteCode = GenerateInviteCode();

        await db.ServerInvites.AddAsync(new ServerInvite
        {
            Id        = EncodeToUlong(inviteCode),
            CreatedAt = DateTime.Now,
            CreatorId = issuer,
            Expired   = DateTime.UtcNow + expiration,
            ServerId  = this.GetPrimaryKey(),
        });
        await db.SaveChangesAsync();
        return new InviteCode(inviteCode);
    }

    public async Task<List<InviteCodeEntity>> GetInviteCodes()
    {
        await using var db = await context.CreateDbContextAsync();

        var list = await db.ServerInvites
           .Where(x => x.ServerId == this.GetPrimaryKey())
           .AsNoTracking()
           .ToListAsync();
        return list.Select(x => new InviteCodeEntity(new InviteCode(DecodeFromUlong(x.Id)), x.ServerId, x.CreatorId, x.Expired, 0)).ToList();
    }


    private unsafe static string GenerateInviteCode(int length = 9)
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