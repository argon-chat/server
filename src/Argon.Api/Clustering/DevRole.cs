namespace Argon.Api.Clustering;

using Argon.Features.Clustering;

/// <summary>
/// Every role at once, in one process.
/// </summary>
/// <remarks>
/// Not a fourth kind of role — it is a composition, the same way the integration suite's role is.
/// The split into <c>core</c>, <c>voice</c>, <c>media</c> and the rest exists so a deployment can
/// scale and isolate them; a person running the product on one machine wants none of that and does
/// want every grain reachable without starting ten processes.
/// <para>
/// It exposes the cluster gateway because it is the only silo there is, and it enables reminders
/// because it hosts the grains that use them. Both follow from including everything.
/// </para>
/// </remarks>
public sealed class DevRole : IArgonRole
{
    public static ArgonRoleId Id => ArgonRoleId.Dev;

    public string Description           => "every role in one process, for local development";
    public bool   IsClient              => false;
    public bool   ExposesClusterGateway => true;
    public bool   UsesReminders         => true;

    public void OnGrainReferences(IGrainCollectionRegistry registry)
    {
        registry.Include<CoreRole>();
        registry.Include<VoiceRole>();
        registry.Include<MediaRole>();
        registry.Include<ModerationRole>();
        registry.Include<CommerceRole>();
        registry.Include<JobsRole>();

        registry.Include<EntryPointRole>();
        registry.Include<BotApiRole>();
        registry.Include<AdminRole>();
        registry.Include<AccountConsoleRole>();
        registry.Include<AegisRole>();
    }
}
