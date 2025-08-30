//namespace Argon.Features.Social;

//using Newtonsoft.Json.Linq;
//using Services;

//public record TelegramSocialOptions
//{
//    public string BotToken { get; set; }
//}

//public class TelegramSocialBounder(IArgonCacheDatabase cache, ILogger<TelegramSocialBounder> logger, IOptions<TelegramSocialOptions> options)
//{
//    public async Task<string> CreateBoundTokenAsync(Guid userId, TimeSpan validity)
//    {
//        var keyId = Guid.NewGuid();
//        await cache.StringSetAsync($"bound_tg_{keyId:N}", userId.ToString(), validity);
//        return keyId.ToString("N");
//    }

//    public async Task<bool> CompleteBoundTokenAsync(Guid userId, string token, string tgUserData, IClusterClient client)
//    {
//        using var scope = logger.BeginScope("Bound telegram user, {userId}, {token}, {tgUserData}", userId, token, tgUserData);

//        var existKey = await cache.StringGetAsync($"bound_tg_{token}");

//        if (string.IsNullOrEmpty(existKey))
//        {
//            logger.LogWarning("Trying create bound telegram user, but key expired or not exist");
//            return false;
//        }

//        if (!Guid.TryParse(existKey, out var targetUserId))
//        {
//            logger.LogWarning("Trying create bound telegram user, but userId is not valid in cache");
//            return false;
//        }

//        if (!userId.Equals(targetUserId))
//        {
//            logger.LogWarning("Trying create bound telegram user, but userId is not matched");
//            return false;
//        }

//        var json = JObject.Parse(tgUserData);

//        if (!Validate(json, options.Value.BotToken))
//        {
//            logger.LogWarning("Trying create bound telegram user, but hash is not valid");
//            return false;
//        }

//        if (!json.TryGetValue("id", out var socialIdObj))
//        {
//            logger.LogWarning("Trying create bound telegram user, but id is not defined in userData");
//            return false;
//        }

//        var socialId = socialIdObj.ToObject<string>();

//        if (string.IsNullOrEmpty(socialId))
//        {
//            logger.LogWarning("Trying create bound telegram user, but id is empty or null");
//            return false;
//        }

//        await client.GetGrain<IUserGrain>(userId).CreateSocialBound(SocialKind.Telegram, tgUserData, socialId);
//        return true;
//    }

//    public static bool Validate(JObject data, string botToken)
//    {
//        if (!data.TryGetValue("hash", out var hashToken))
//            return false;

//        var receivedHash = hashToken.ToString();
//        if (string.IsNullOrEmpty(receivedHash))
//            return false;

//        Span<byte> computedHex = stackalloc byte[64];
//        Span<byte> hashBytes   = stackalloc byte[32];
//        Span<byte> secret      = stackalloc byte[32];


//        var dataCheck = data.Properties()
//           .Where(p => p.Name != "hash")
//           .OrderBy(p => p.Name, StringComparer.Ordinal)
//           .Select(p => $"{p.Name}={p.Value}");

//        var dataCheckString = string.Join('\n', dataCheck);

//        Span<byte> tokenBytes = Encoding.UTF8.GetBytes(botToken);

//        SHA256.HashData(tokenBytes, secret);

//        ReadOnlySpan<byte> dataBytes = Encoding.UTF8.GetBytes(dataCheckString);

//        HMACSHA256.HashData(secret, dataBytes, hashBytes);

//        for (var i = 0; i < hashBytes.Length; i++)
//        {
//            var b = hashBytes[i];
//            computedHex[i * 2]     = (byte)ToHexCharLower(b >> 4);
//            computedHex[i * 2 + 1] = (byte)ToHexCharLower(b & 0xF);
//        }

//        return Encoding.UTF8.GetString(computedHex) == receivedHash;
//    }

//    private static char ToHexCharLower(int val)
//        => (char)(val < 10 ? '0' + val : 'a' + (val - 10));
//}