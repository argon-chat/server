namespace Argon.Services;

using StackExchange.Redis;

public static class RedisErrorClassifier
{
    private static readonly string[] RetryablePrefixes =
    [
        "READONLY", "LOADING", "MASTERDOWN", "CLUSTERDOWN", "MOVED", "ASK"
    ];

    public static bool IsReplicaWriteError(Exception? ex, ILogger logger)
    {
        if (ex == null)
        {
            logger.LogError("RedisErrorClassifier: Received NULL exception");
            return false;
        }

        var msg = ex.Message?.ToUpperInvariant() ?? "";
        logger.LogWarning("RedisErrorClassifier: Exception caught: {Type}: {Message}", ex.GetType().Name, msg);

        switch (ex)
        {
            case RedisServerException serverEx:
            {
                logger.LogWarning("RedisErrorClassifier: RedisServerException → checking prefixes...");

                var matched = RetryablePrefixes.Any(prefix =>
                {
                    var ok = msg.StartsWith(prefix);
                    if (ok)
                    {
                        logger.LogWarning("RedisErrorClassifier: MATCH prefix '{Prefix}'", prefix);
                    }

                    return ok;
                });

                if (matched)
                    return true;

                logger.LogWarning("RedisErrorClassifier: NO MATCH in RedisServerException");
                break;
            }
            case RedisConnectionException connEx:
            {
                logger.LogWarning("RedisErrorClassifier: RedisConnectionException → checking prefixes...");

                var matched = RetryablePrefixes.Where(prefix =>
                    {
                        var ok = msg.Contains(prefix);
                        if (ok)
                        {
                            logger.LogWarning("RedisErrorClassifier: MATCH prefix '{Prefix}' (contains)", prefix);
                        }

                        return ok;
                    })
                   .Any();

                if (matched)
                    return true;

                logger.LogWarning("RedisErrorClassifier: NO MATCH in RedisConnectionException");
                break;
            }
        }

        if (ex.InnerException != null)
        {
            logger.LogWarning("RedisErrorClassifier: Checking InnerException...");
            return IsReplicaWriteError(ex.InnerException, logger);
        }

        logger.LogWarning("RedisErrorClassifier: No retryable prefix matched.");
        return false;
    }
}