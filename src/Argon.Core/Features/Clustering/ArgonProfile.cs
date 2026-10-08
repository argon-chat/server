namespace Argon.Features.Clustering;

using Argon.Api.Features.Utils;
using Argon.Features.NatsStreaming;
using Argon.Services.Ion;

/// <summary>
/// Argon's side of the Orleans host: the storage its grains declare, the converters its contracts
/// need on the wire, the bus every role carries and the grains a silo wakes at start.
/// </summary>
public static class ArgonProfile
{
    public static ArgonOrleansProfile Orleans { get; } = Build();

    private static ArgonOrleansProfile Build()
    {
        var profile = new ArgonOrleansProfile();

        profile.StorageProviders.AddRange([IUserSessionGrain.StorageId, IServerInvitesGrain.StorageId, "meets"]);

        profile.Serializer.Add(settings =>
        {
            settings.Converters.Add(new MessageEntityConverter());
            // A union held inside an object is Newtonsoft's to write, and IonUnionTypeFilter does not
            // reach it. Every union that can sit in a grain argument's graph is listed.
            settings.Converters.Add(new IonUnionConverter<IWornCosmetic>());
            settings.Converters.Add(new IonUnionConverter<ICosmeticPayload>());
            settings.Converters.Add(new UlongEnumConverter<ArgonEntitlement>());
        });

        profile.Host.Add(builder => builder.AddNatsCtx());
        profile.ForeignClientServices.Add(typeof(NatsContext));

        // The declaration in the role drives validation (E5); the action itself still has to name
        // the grain and the method, so it stays explicit and gated on the declaration.
        profile.OnStartup<IAutoDeleteSchedulerGrain>(IAutoDeleteSchedulerGrain.SingletonId, g => g.EnsureSchedulerActiveAsync())
           .OnStartup<ITtlSweepGrain>(ITtlSweepGrain.SingletonId, g => g.EnsureSweeperActiveAsync())
           .OnStartup<ISessionRegistryFlushGrain>(ISessionRegistryFlushGrain.SingletonId, g => g.EnsureActiveAsync())
           .OnStartup<IConnectionsMaintenanceGrain>(IConnectionsMaintenanceGrain.SingletonId, g => g.EnsureActiveAsync());

        return profile;
    }
}
