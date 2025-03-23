namespace Argon.Features.RegionalUnit;

using System;
using Env;
using Vault;
using Consul;
using Logging;
using Newtonsoft.Json;
using Serilog;

public class RegionalUnitApp
{
    public const string UNIT_DI_CONTAINER = "$unit_container";

    public async static Task<WebApplicationBuilder> CreateBuilder(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // for compatibility, it is overwritten to correct value when necessary
        if (builder.IsEntryPointRole())
            builder.WebHost.UseUrls("http://localhost:7829");
        else if (builder.IsGatewayRole())
            builder.WebHost.UseUrls("http://localhost:7822");
        else
            builder.WebHost.UseUrls("http://localhost:7854");

        if (builder.Environment.IsSingleInstance())
        {
            // set default dc for compatible reason
            builder.SetDatacenter("ru-3");
            builder.Services.AddKeyedSingleton<string>("dc", "ru-3"); 
            return builder;
        }

        var entryBuilder = WebApplication.CreateBuilder(args);

        entryBuilder.AddLogging();
        entryBuilder.AddVaultConfiguration(false);

        var key = entryBuilder.Environment.DetermineClientSpace();

        entryBuilder.Services.Configure<ConsulClientConfiguration>(entryBuilder.Configuration.GetSection($"Orleans:{key}"));
        entryBuilder.Services.AddSingleton<IConsulClient>(q => new ConsulClient(q.GetRequiredService<IOptions<ConsulClientConfiguration>>().Value));

        var app = entryBuilder.Build();

        app.StartAsync().ConfigureAwait(false);
        await Task.Delay(1000);
        await app.StopAsync();

        Log.Logger.Warning($"Orleans:{key}");
        foreach (var v in app.Configuration.AsEnumerable())
        {
            Log.Logger.Warning($"kv: {v.Key} -> {v.Value}");
        }
        var unitContainer = app.Services;

        var consul = unitContainer.GetRequiredService<IConsulClient>();

        var agent = await consul.Agent.Self();

        var dc = agent.Response["Config"]["Datacenter"] as string ?? 
            throw new InvalidOperationException($"Invalid response from consul");

        builder.Services.AddKeyedSingleton(UNIT_DI_CONTAINER, unitContainer);
        builder.Services.Add(new ServiceDescriptor(typeof(IConsulClient), _ => unitContainer.GetRequiredService<IConsulClient>(), ServiceLifetime.Singleton));
        builder.Services.AddSingleton<IArgonClusterRouter, ClusterRouter>();
        builder.SetDatacenter(dc);
        builder.Services.AddKeyedSingleton("dc", dc);

        if (!builder.Environment.IsMultiRegion())
            return builder;

        var allDatacenters = await consul.Catalog.Datacenters();

        if (allDatacenters.Response.Length == 0)
            throw new InvalidOperationException($"No datacenter available found");
        if (allDatacenters.Response.Length == 1)
            throw new InvalidOperationException($"Single datacenter not support in Multi Regional mode");

        var pos = await consul.KV.Get($"region/pos");

        if (pos.Response is null)
            throw new InvalidOperationException($"No defined argon configuration for '{dc}' datacenter");

        var jsonData = JsonConvert.DeserializeObject<ArgonUnitDto>(Encoding.UTF8.GetString(pos.Response.Value))!;

        builder.Services.AddSingleton<ArgonUnitOptions>(_ => new ArgonUnitOptions(dc, key, jsonData.pos, IPAddress.Parse(jsonData.ip)));
        return builder;
    }
}
