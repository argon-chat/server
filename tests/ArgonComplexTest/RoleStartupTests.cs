namespace ArgonComplexTest;

using Argon.Api.Clustering;
using Argon.Features.Clustering;
using ArgonComplexTest.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Orleans;
using Orleans.Configuration;

[TestFixture, NonParallelizable]
public class RoleStartupTests
{
    private const int FirstSiloPort = 21111;

    private static ArgonTestHostSettings Settings
        => ArgonTestEnvironment.Instance.Host.Settings;

    private static IEnumerable<ArgonRoleId> SiloRoles()
        =>
        [
            ArgonRoleId.Core, ArgonRoleId.Voice, ArgonRoleId.Media, ArgonRoleId.Moderation,
            ArgonRoleId.Commerce, ArgonRoleId.Jobs
        ];

    private static IEnumerable<ArgonRoleId> ClientRoles()
        => [ArgonRoleId.EntryPoint, ArgonRoleId.BotApi, ArgonRoleId.Admin];

    private static RoleDescriptor Describe(ArgonRoleId id)
        => ArgonClusterCatalog.Build().Require(id);

    [Test, TestCaseSource(nameof(SiloRoles)), CancelAfter(300_000)]
    public async Task A_silo_role_starts_and_hosts_exactly_what_it_declared(ArgonRoleId id)
    {
        var role = Describe(id);
        Assert.That(role.IsClient, Is.False, "premise: this is a silo role");

        var port = FirstSiloPort + SiloRoles().ToList().IndexOf(id) * 10;

        await using var host = new RoleHost(Settings, id, port, $"argon-test-{id.Value}");

        var services = host.Services;

        Assert.Multiple(() =>
        {
            Assert.That(services.GetRequiredService<RoleDescriptor>().Id, Is.EqualTo(id));

            var hosted = services.GetRequiredService<IOptions<GrainTypeOptions>>().Value.Classes;
            var ours   = hosted.Where(t => t.Assembly.GetName().Name?.StartsWith("Argon") is true).ToArray();

            Assert.That(ours, Is.EquivalentTo(role.HostedGrains),
                $"GrainTypeOptions.Classes must carry exactly the declared grains for '{id}'");
            Assert.That(hosted.Count, Is.GreaterThan(ours.Length),
                "the runtime's own grain classes must have survived the filter");

            Assert.That(services.GetService<IGrainFactory>(), Is.Not.Null, "a silo exposes a grain factory");
        });
    }

    [Test, TestCaseSource(nameof(ClientRoles)), CancelAfter(300_000)]
    public async Task A_client_role_starts_and_hosts_no_grains(ArgonRoleId id)
    {
        var role = Describe(id);
        Assert.Multiple(() =>
        {
            Assert.That(role.IsClient, Is.True, "premise: this is a client role");
            Assert.That(role.HostedGrains, Is.Empty);
        });

        await using var host = new RoleHost(Settings, id, siloPort: 0, ArgonClusterEndpoints.DefaultClusterId);

        var services = host.Services;

        Assert.Multiple(() =>
        {
            Assert.That(services.GetRequiredService<RoleDescriptor>().Id, Is.EqualTo(id));
            Assert.That(services.GetService<IClusterClient>(), Is.Not.Null, "a client role connects to the cluster");
        });
    }

    [Test, CancelAfter(300_000)]
    public async Task Features_declared_by_a_role_have_registered_their_services()
    {
        await using var host = new RoleHost(Settings, ArgonRoleId.EntryPoint, siloPort: 0,
            ArgonClusterEndpoints.DefaultClusterId);

        var services = host.Services;
        var role     = services.GetRequiredService<RoleDescriptor>();

        Assert.Multiple(() =>
        {
            Assert.That(role.Features.Ordered.Select(f => f.Name), Does.Contain("ion").And.Contain("app-hub"));

            Assert.That(services.GetService<Microsoft.AspNetCore.SignalR.IHubContext<Argon.Core.Features.Transport.AppHub>>(),
                Is.Not.Null, "app-hub feature did not run");

            Assert.That(role.Features.Ordered.Select(f => f.Name),
                Does.Contain("jwt").And.Contain("cache").And.Contain("vault"));
        });
    }

    [Test, CancelAfter(300_000)]
    public async Task Only_moderation_registers_the_onnx_stack()
    {
        await using var moderation = new RoleHost(Settings, ArgonRoleId.Moderation, FirstSiloPort + 200, "argon-test-moderation");
        await using var media      = new RoleHost(Settings, ArgonRoleId.Media, FirstSiloPort + 210, "argon-test-media");

        Assert.Multiple(() =>
        {
            Assert.That(moderation.Services.GetService<Argon.Features.Moderation.IContentModerationService>(),
                Is.Not.Null, "moderation must have the classifier");
            Assert.That(media.Services.GetService<Argon.Features.Moderation.IContentModerationService>(),
                Is.Null, "media must not — the models are resident for the process lifetime, so " +
                         "linking the feature anywhere else costs that memory in every replica of it");

            Assert.That(media.Services.GetService<Argon.Features.Storage.IS3StorageService>(), Is.Not.Null);
            Assert.That(moderation.Services.GetService<Argon.Features.Storage.IS3StorageService>(), Is.Not.Null,
                "the classifier fetches the object it classifies");
        });
    }
}
