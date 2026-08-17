namespace ArgonComplexTest.Infrastructure;

using Argon.Api.Clustering;
using Argon.Features.Clustering;

public sealed class IntegrationTestRole : IArgonRole
{
    public static ArgonRoleId Id => new("integration");

    public string Description           => "all roles co-hosted, for the integration suite";
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
    }
}
