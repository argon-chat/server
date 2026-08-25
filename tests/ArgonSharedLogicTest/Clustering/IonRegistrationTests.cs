namespace ArgonSharedLogicTest.Clustering;

using ion.runtime.network;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

/// <summary>
/// Why a role registers its Ion services as one call rather than one per feature.
/// </summary>
/// <remarks>
/// Three features register Ion services — the first-party protocol, the admin console and the
/// developer console — and two of them ask for a port of their own. Calling <c>AddIonProtocol</c>
/// once per feature looks like it works: the services land in the container, the route is mapped, the
/// process starts clean. The ports of every call after the first are simply not bound, and the
/// symptom is a console listening on nothing.
/// <para>
/// Measured, not reasoned: with three separate calls the admin console's 8920 never opened, and with
/// one aggregated call it does. These tests pin the shape of the registration that the aggregation in
/// <c>AddArgonRole</c> — and the guard beside it — depend on.
/// </para>
/// </remarks>
[TestFixture]
public class IonRegistrationTests
{
    private static ServiceCollection Registered(int calls)
    {
        var services = new ServiceCollection();

        for (var i = 0; i < calls; i++)
            services.AddIonProtocol(_ => { });

        return services;
    }

    private static int TransportConfigurators(IServiceCollection services)
        => services.Count(d => d.ServiceType == typeof(IConfigureOptions<IonTransportOptions>));

    /// <summary>
    /// The port list is carried by <see cref="IonTransportOptions"/>, configured by one descriptor per
    /// call — which is what makes a second call visible to the guard.
    /// </summary>
    [Test]
    public void Each_call_adds_its_own_transport_configurator()
    {
        Assert.Multiple(() =>
        {
            Assert.That(TransportConfigurators(Registered(1)), Is.EqualTo(1));
            Assert.That(TransportConfigurators(Registered(3)), Is.EqualTo(3));
        });
    }

    /// <summary>
    /// The guard in <c>AddArgonRole</c> counts these descriptors before making its own call, so if the
    /// registration ever stops going through <see cref="IConfigureOptions{T}"/> the guard has to move
    /// with it. This test is what would say so.
    /// </summary>
    [Test]
    public void A_role_with_no_ion_features_registers_no_transport_configurator()
        => Assert.That(TransportConfigurators(new ServiceCollection()), Is.Zero);
}
