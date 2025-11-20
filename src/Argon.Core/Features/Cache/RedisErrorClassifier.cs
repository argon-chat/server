namespace Argon.Services;

using StackExchange.Redis;

public static class RedisErrorClassifier
{
    public static bool IsReplicaWriteError(Exception ex)
    {
        if (ex is RedisServerException s)
        {
            var msg = s.Message ?? string.Empty;
            if (msg.StartsWith("READONLY", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        if (ex is RedisConnectionException c)
        {
            var msg = c.Message ?? string.Empty;
            if (msg.Contains("requires writable - not eligible for replica",
                    StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}