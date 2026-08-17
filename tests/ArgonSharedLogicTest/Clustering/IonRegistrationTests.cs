namespace ArgonSharedLogicTest.Clustering;

using ion.runtime.network;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Why a role registers its Ion services as one call rather than one per feature.
/// </summary>
/// <remarks>
/// Three features register Ion services — the first-party protocol, the admin console and the
/// developer console — and two of them ask for a port of their own. Calling <c>AddIonProtocol</c>
/// once per feature looks like it works: the services land in the container, the route is mapped, the
/// process starts clean. Only the ports go missing, and only for every call after the first.
/// </remarks>
[TestFixture]
public class IonRegistrationTests
{
    [Test]
    public void AddIonProtocol_installs_the_port_registry_once_and_ignores_later_calls()
    {
        var services = new ServiceCollection();

        services.AddIonProtocol(_ => { });
        services.AddIonProtocol(_ => { });

        var registries = services.Where(d => d.ServiceType == typeof(IonPortBindingRegistry)).ToArray();

        Assert.That(registries, Has.Length.EqualTo(1),
            "the registry is added with TryAdd; this is the premise the aggregation in AddArgonRole " +
            "rests on, and the guard there watches for a feature that called this itself");
    }

    /// <summary>
    /// The guard in <c>AddArgonRole</c> looks for exactly this descriptor, so if the registration
    /// moves the guard has to move with it.
    /// </summary>
    [Test]
    public void The_port_registry_is_registered_under_its_own_type()
    {
        var services = new ServiceCollection();

        services.AddIonProtocol(_ => { });

        Assert.That(services.Any(d => d.ServiceType == typeof(IonPortBindingRegistry)), Is.True);
    }
}
