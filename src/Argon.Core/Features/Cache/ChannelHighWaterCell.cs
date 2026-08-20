namespace Argon.Features.Cache;

/// <summary>
/// The Redis cell that carries a channel's highest message id between the activation that mints it
/// and everything that needs it fresh.
/// </summary>
/// <remarks>
/// <para>The durable copy is the channel row, written once per flush rather than once per message.
/// That is the whole point of coalescing, and it means the row lags by up to one flush interval —
/// so anything answering "is there something newer than my cursor" reads this cell first and falls
/// back to the row. The cell is written on every send and costs microseconds; the row is the
/// backstop that survives an eviction.</para>
///
/// <para>The key lived in three files before this type existed — the grain that writes it, the
/// bootstrap that reads it, and the badge query — and nothing in the type system connected them. A
/// silent disagreement between two of those spellings does not fail a build or a test; it just makes
/// unread badges quietly wrong for whoever hits the path that guessed differently. One function is
/// the fix.</para>
///
/// <para>Read it through <see cref="ConnectionMultiplexer"/>'s default database for the
/// <c>RedisProfiles.Cache</c> pool, not an explicit index: the writer uses the pool default, and a
/// reader that names a database would look in the wrong one and see nothing — which reads as "this
/// channel has no messages" rather than as an error.</para>
/// </remarks>
public static class ChannelHighWaterCell
{
    public static string KeyFor(Guid channelId) => $"chan:last:{channelId}";
}
