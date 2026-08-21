//namespace Argon.Features.Journaling;

//using System.Text.Json;
//using Services;
//using StackExchange.Redis;

//public interface IStateSyncService<TEvent, TState>
//{
//    Task<long>                                      AppendEventAsync(TEvent ev);
//    Task<IReadOnlyList<(long Revision, TEvent Ev)>> GetDeltasAsync(long fromRevision);
//    Task<TState>                                    GetSnapshotAsync();
//    Task                                            SaveSnapshotAsync(TState state, long revision);
//}

//public class RedisStateSyncService<TEvent, TState>(IRedisPoolConnections pool, string name) : IStateSyncService<TEvent, TState>
//{
//    private readonly string                streamKey   = $"{name}:events";
//    private readonly string                snapshotKey = $"{name}:snapshot";
//    private readonly JsonSerializerOptions settings    = new(JsonSerializerDefaults.Web);

//    public async Task<long> AppendEventAsync(TEvent ev)
//    {
//        using var conn = pool.Rent();
//        var       db   = conn.GetDatabase();

//        var payload = JsonSerializer.Serialize(ev, settings);
//        var entryId = await db.StreamAddAsync(
//            streamKey,
//            [new NameValueEntry("payload", payload)],
//            maxLength: 20,
//            useApproximateMaxLength: true
//        );

//        return ParseRevision(entryId);
//    }

//    public async Task<IReadOnlyList<(long Revision, TEvent Ev)>> GetDeltasAsync(long fromRevision)
//    {
//        using var conn = pool.Rent();
//        var       db   = conn.GetDatabase();

//        var entries = await db.StreamReadAsync(streamKey, $"{fromRevision + 1}-0");
//        return entries.Select(e =>
//        {
//            var ev = JsonSerializer.Deserialize<TEvent>(e.Values.First().Value!, settings)!;
//            return (ParseRevision(e.Id), ev);
//        }).ToList();
//    }

//    public async Task<TState> GetSnapshotAsync()
//    {
//        using var conn = pool.Rent();
//        var       db   = conn.GetDatabase();

//        var json = await db.StringGetAsync(snapshotKey);
//        return json.HasValue
//            ? JsonSerializer.Deserialize<TState>(json!, this.settings)!
//            : default!;
//    }

//    public async Task SaveSnapshotAsync(TState state, long revision)
//    {
//        using var conn = pool.Rent();
//        var       db   = conn.GetDatabase();

//        var json = JsonSerializer.Serialize(state, this.settings);
//        await db.StringSetAsync(snapshotKey, json);
//    }

//    private static long ParseRevision(RedisValue id)
//    {
//        var parts = id.ToString().Split('-');
//        return long.Parse(parts[0]);
//    }
//}