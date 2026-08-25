namespace ArgonSharedLogicTest.Aegis;

using Argon.Features.Aegis;
using Argon.Grains.Interfaces;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Runtime;

/// <summary>
/// The cache in front of the identity server's lookups, and the line between what may be held and
/// what may not.
/// </summary>
/// <remarks>
/// A cache that answers a question nobody asked is not a stale answer, it is the wrong one. Two of
/// these lookups are shaped so that caching them naively does exactly that: the consent screen's
/// scopes come from the request being authorized right now, and whether a person may sign into an
/// application depends on the person. Both were held under a key that mentioned only the client id
/// in the service this was carried over from.
/// </remarks>
[TestFixture]
public class AegisDirectoryTests
{
    private static (AegisDirectory directory, RecordingGrains grains) Subject()
    {
        var services = new ServiceCollection();
        services.AddHybridCache();

        var provider = services.BuildServiceProvider();
        var grains   = new RecordingGrains();

        return (new AegisDirectory(grains, provider.GetRequiredService<HybridCache>()), grains);
    }

    [Test]
    public async Task The_consent_screen_shows_the_scopes_this_request_asked_for()
    {
        var (directory, _) = Subject();

        var first  = await directory.GetAppInfoAsync("client-a", ["user.read"]);
        var second = await directory.GetAppInfoAsync("client-a", ["email", "role"]);

        Assert.Multiple(() =>
        {
            Assert.That(first!.RequestedScopes, Is.EqualTo(new[] { "user.read" }));

            // Cached under the client id alone, so a second authorization of the same application
            // would have been shown the first one's scope list.
            Assert.That(second!.RequestedScopes, Is.EqualTo(new[] { "email", "role" }));
        });
    }

    [Test]
    public async Task An_application_is_only_looked_up_once()
    {
        var (directory, grains) = Subject();

        await directory.GetAppInfoAsync("client-a", ["user.read"]);
        await directory.GetAppInfoAsync("client-a", ["email"]);

        Assert.That(grains.Apps.AppInfoCalls, Is.EqualTo(1),
            "attaching the requested scopes must not cost a second round trip");
    }

    [Test]
    public async Task Two_applications_do_not_share_an_entry()
    {
        var (directory, _) = Subject();

        var a = await directory.GetAppInfoAsync("client-a", []);
        var b = await directory.GetAppInfoAsync("client-b", []);

        Assert.That(a!.AppName, Is.Not.EqualTo(b!.AppName));
    }

    /// <summary>
    /// Whether a person may sign into an application is about the pair, and the pair changes with
    /// every visitor.
    /// </summary>
    [Test]
    public async Task Whether_a_user_may_sign_in_is_asked_every_time()
    {
        var (directory, grains) = Subject();

        await directory.CanSignInAsync("client-a", Guid.NewGuid());
        await directory.CanSignInAsync("client-a", Guid.NewGuid());

        Assert.That(grains.Apps.SignInChecks, Is.EqualTo(2));
    }

    // ── stubs ────────────────────────────────────────────────────────────────────────────────

    private sealed class RecordingApps : IAppsManagementGrain
    {
        public int AppInfoCalls  { get; private set; }
        public int SignInChecks  { get; private set; }

        public Task<BotCredentialsInfo?> GetCredentialsForBotAsync(string clientId, CancellationToken ct = default)
            => Task.FromResult<BotCredentialsInfo?>(new BotCredentialsInfo(clientId, "secret", [], [], false, false));

        public Task<LoginAllowedResult> CanBeLoginForAppAsync(string clientId, Guid userId, CancellationToken ct = default)
        {
            SignInChecks++;
            return Task.FromResult(new LoginAllowedResult(true, null));
        }

        public Task<OAuthAppInfo?> GetOAuthAppInfoAsync(
            string clientId, IReadOnlyList<string> requestedScopes, CancellationToken ct = default)
        {
            AppInfoCalls++;

            return Task.FromResult<OAuthAppInfo?>(new OAuthAppInfo(
                Guid.NewGuid(), $"app for {clientId}", null, null, "team", null, true, false, requestedScopes));
        }
    }

    /// <summary>
    /// Only the two overloads <c>GetGrain&lt;T&gt;(Guid)</c> resolves to are reachable; the rest of
    /// <see cref="IGrainFactory"/> is here because the interface is wide, not because it is used.
    /// </summary>
    private sealed class RecordingGrains : IGrainFactory
    {
        public RecordingApps Apps { get; } = new();

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidKey
            => (TGrainInterface)(object)(typeof(TGrainInterface) == typeof(IAppsManagementGrain)
                ? Apps
                : throw new NotSupportedException($"no stub for {typeof(TGrainInterface).Name}"));

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerKey
            => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(string primaryKey, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithStringKey
            => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(Guid primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithGuidCompoundKey
            => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(long primaryKey, string keyExtension, string? grainClassNamePrefix = null)
            where TGrainInterface : IGrainWithIntegerCompoundKey
            => throw new NotSupportedException();

        public TGrainObserverInterface CreateObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver
            => throw new NotSupportedException();

        public void DeleteObjectReference<TGrainObserverInterface>(IGrainObserver obj)
            where TGrainObserverInterface : IGrainObserver
            => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey)         => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey)         => throw new NotSupportedException();
        public IGrain GetGrain(Type grainInterfaceType, string grainPrimaryKey)       => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, Guid grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        public IGrain GetGrain(Type grainInterfaceType, long grainPrimaryKey, string keyExtension)
            => throw new NotSupportedException();

        public TGrainInterface GetGrain<TGrainInterface>(GrainId grainId) where TGrainInterface : IAddressable
            => throw new NotSupportedException();

        public IAddressable GetGrain(GrainId grainId)                                   => throw new NotSupportedException();
        public IAddressable GetGrain(GrainId grainId, GrainInterfaceType interfaceType) => throw new NotSupportedException();

        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainKey, string grainClassNamePrefix)
            => throw new NotSupportedException();

        public IAddressable GetGrain(Type grainInterfaceType, IdSpan grainKey) => throw new NotSupportedException();
    }
}
