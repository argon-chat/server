namespace Argon.Api.Features.Orleans.Consul;

using System.Text.Json;
using global::Consul;
using global::Orleans.GrainDirectory;
using global::Orleans.Runtime;
using Microsoft.Extensions.Options;

public static class ConsulEx
{
    public static void Assert(this WriteResult result)
    {
        if (result.StatusCode != HttpStatusCode.OK)
            throw new Exception();
    }
}

public class ConsulDirectory(IConsulClient client, IOptions<ConsulDirectoryOptions> opt, ILogger<IGrainDirectory> logger) : IGrainDirectory
{
    private const string ConsulPrefix = "orleans/grains/{0}/{1}";

    private readonly ConcurrentDictionary<SiloAddress, string> Sessions = new();

    private async Task<string> EnsureSiloSession(SiloAddress address)
    {
        if (Sessions.TryGetValue(address, out var session))
            return session;

        var s = await client.Session.Create(new SessionEntry()
        {
            Name     = address.ToString(),
            Behavior = SessionBehavior.Delete,
            Checks = [$"UpdateIAmAlive.{address}", "serfHealth"],
            LockDelay = TimeSpan.Zero
        });

        if (s.StatusCode != HttpStatusCode.OK)
            throw new Exception($"Cannot create Silo Session");

        if (Sessions.TryAdd(address, s.Response))
            return s.Response;
        if (Sessions.TryGetValue(address, out session))
        {
            await client.Session.Destroy(s.Response);
            return session;
        }

        throw new Exception();
    }

    public async Task<GrainAddress?> Register(GrainAddress address)
    {
        var consulKey = ToPath(address.GrainId);
        var json      = JsonSerializer.Serialize(address);
        var value     = Encoding.UTF8.GetBytes(json);
        var session   = await EnsureSiloSession(address.SiloAddress!);
        var kvPair = new KVPair(consulKey)
        {
            Value   = value,
            Session = session
        };
        await client.KV.Acquire(kvPair);
        return address;
    }

    public Task Unregister(GrainAddress address)
        => client.KV.Delete(ToPath(address.GrainId));

    public async Task<GrainAddress?> Lookup(GrainId grainId)
    {
        var consulKey = ToPath(grainId);
        var result    = await client.KV.Get(consulKey);
        if (result.StatusCode != HttpStatusCode.OK)
            return null;
        if (result.Response == null)
            return null;

        var json = Encoding.UTF8.GetString(result.Response.Value);
        return JsonSerializer.Deserialize<GrainAddress>(json);
    }

    public async Task UnregisterSilos(List<SiloAddress> siloAddresses)
    {
        var list = await client.Session.List();

        foreach (var v in list.Response)
        {
            foreach (var _ in siloAddresses.Where(address => v.Name.Equals(address.ToString())))
            {
                var deleteResult = await client.Session.Destroy(v.ID);

                if (deleteResult.StatusCode != HttpStatusCode.OK)
                    throw new Exception();
            }
        }
    }


    private string ToPath(GrainId grainId)
        => string.Format(ConsulPrefix, opt.Value.Directory, grainId);
}

public record ConsulDirectoryOptions
{
    public string Directory { get; set; }
}