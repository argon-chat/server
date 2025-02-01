namespace Argon.Api.Features.Orleans.Consul;

using System.Text.Json;
using global::Consul;
using global::Consul.Filtering;
using global::Orleans.Configuration;

public class ConsulMembership(IConsulClient client, ILogger<IMembershipTable> logger, IOptions<ClusterOptions> clusterOptions) : IMembershipTable
{
    private readonly JsonSerializerOptions opt = new(JsonSerializerOptions.Web)
    {
        IncludeFields = true
    };

    private async Task<TableVersion> GetTableVersion()
    {
        var ee = await client.KV.Get(ConsulOrleansTableVersion.Path);

        if (ee.StatusCode == HttpStatusCode.OK)
            return JsonSerializer.Deserialize<ConsulOrleansTableVersion>(Encoding.UTF8.GetString(ee.Response.Value), opt)!.ToTable();
        var table = new TableVersion(0, "");
        await client.KV.Put(new KVPair(ConsulOrleansTableVersion.Path)
        {
            Value = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ConsulOrleansTableVersion.Create(table), opt))
        });
        return table;
    }

    private async Task UpdateTableVersion(TableVersion version)
        => await client.KV.Put(new KVPair(ConsulOrleansTableVersion.Path)
        {
            Value = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ConsulOrleansTableVersion.Create(version), opt))
        });

    public Task InitializeMembershipTable(bool tryInitTableVersion) // not supported
        => Task.CompletedTask;

    public Task DeleteMembershipTableEntries(string clusterId) // not supported
        => Task.CompletedTask;

    public Task CleanupDefunctSiloEntries(DateTimeOffset beforeDate) // not supported
        => Task.CompletedTask;

    public async Task<MembershipTableData> ReadRow(SiloAddress key)
    {
        var idSelector = new StringFieldSelector("ID");

        var services = await client.Agent.Services(idSelector == key.ToString());

        if (services.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Selector ServiceID == '{key.ToString()}' return '{services.StatusCode}'");

        return services.Response.Count switch
        {
            0   => throw new InvalidOperationException($"Selector ServiceID == '{key.ToString()}' return empty list"),
            > 1 => throw new InvalidOperationException($"Selector ServiceID == '{key.ToString()}' return multiple services"),
            _ => new MembershipTableData(new Tuple<MembershipEntry, string>(EjectEntry(services.Response.First().Value), ""),
                await GetTableVersion())
        };
    }

    public async Task<MembershipTableData> ReadAll()
    {
        var services = await client.Health.Service("Silo", "silo", true);

        var list = services.Response
           .Select(x => new Tuple<MembershipEntry, string>(EjectEntry(x.Service), ""))
           .ToList();

        return new MembershipTableData(list, await GetTableVersion());
    }


    public async Task<bool> InsertRow(MembershipEntry entry, TableVersion tableVersion)
    {
        var service = new AgentServiceRegistration()
        {
            Name    = "Silo",
            Address = entry.SiloAddress.Endpoint.Address.ToString(),
            Port    = entry.SiloAddress.Endpoint.Port,
            ID      = entry.SiloAddress.ToString(),
            Tags    = [entry.HostName, entry.RoleName, entry.SiloName, "silo"],
            Meta    = GenerateMeta(entry),
            Checks =
            [
                new AgentServiceCheck
                {
                    TCP                            = entry.SiloAddress.Endpoint.ToString(),
                    Name                           = "tcp silo check",
                    Interval                       = TimeSpan.FromSeconds(15),
                    DeregisterCriticalServiceAfter = TimeSpan.FromSeconds(15)
                },
                new AgentServiceCheck
                {
                    CheckID                        = $"UpdateIAmAlive.{entry.SiloAddress}",
                    TTL                            = TimeSpan.FromSeconds(15),
                    DeregisterCriticalServiceAfter = TimeSpan.FromSeconds(15)
                }
            ]
        };


        var sr = await client.Agent.ServiceRegister(service);

        sr.Assert();

        var currentTable = await GetTableVersion();

        await UpdateTableVersion(new TableVersion(tableVersion.Version + 1, currentTable.VersionEtag));

        await UpdateIAmAlive(entry);

        return true;
    }

    public Task<bool> UpdateRow(MembershipEntry entry, string etag, TableVersion tableVersion)
        => InsertRow(entry, tableVersion);

    public async Task UpdateIAmAlive(MembershipEntry entry)
        => await client.Agent.UpdateTTL($"UpdateIAmAlive.{entry.SiloAddress}", "Silo answered correctly!", TTLStatus.Pass);

    private MembershipEntry EjectEntry(AgentService service)
    {
        if (service.Meta.TryGetValue("json", out var json))
        {
            var s = JsonSerializer.Deserialize<MembershipEntry>(json, opt)!;
            s.IAmAliveTime = DateTime.Now - TimeSpan.FromSeconds(10);
            return s;
        }
        throw new InvalidOperationException($"AgentService do not contains json meta");
    }

    private Dictionary<string, string> GenerateMeta(MembershipEntry entry)
        => new()
        {
            {
                "json", JsonSerializer.Serialize(entry, opt)
            },
            {
                "gen", entry.SiloAddress.Generation.ToString()
            },
            {
                "addr", JsonSerializer.Serialize(entry.SiloAddress, opt)
            }
        };
}