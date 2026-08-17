namespace ArgonComplexTest;

using ArgonContracts;
using Argon.Core.Grains.Interfaces;
using Argon.Features.Testing;
using ArgonComplexTest.Infrastructure;
using Bogus;
using ion.runtime.client;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;
using Argon.Grains.Interfaces;

/// <summary>
/// Base class for fixtures that talk to a real Argon server.
/// <para>
/// The server and its containers are <em>not</em> owned here — they belong to
/// <see cref="ArgonTestEnvironment"/> and are shared by the whole assembly. What each fixture owns
/// is its client-side identity: its own <see cref="IonClient"/> and
/// <see cref="DefaultHeaderInterceptor"/>, so that fixtures running concurrently never see each
/// other's bearer token.
/// </para>
/// <para>
/// Tests inside one fixture still run sequentially (see <c>AssemblyInfo.cs</c>), which is what lets
/// <see cref="FakedTestCreds"/> and the ambient token stay simple mutable fields. For scenarios that
/// need two identities at once, use <see cref="CreateSessionAsync"/> instead of the ambient token.
/// </para>
/// </summary>
public abstract class TestBase
{
    private DefaultHeaderInterceptor _interceptor = null!;

    /// <summary>The device id this fixture's client sends — see <see cref="DefaultHeaderInterceptor.MachineId"/>.</summary>
    protected string MachineId => _interceptor.MachineId;

    protected ArgonServerTargetHost FactoryAsp => ArgonTestEnvironment.Instance.Host;
    protected HttpClient            HttpClient => ArgonTestEnvironment.Instance.HttpClient;
    protected IonClient             IonClient  = null!;

    protected NewUserCredentialsInputForTest FakedTestCreds = null!;

    /// <summary>
    /// Runs before any <c>[OneTimeSetUp]</c> a derived fixture declares — NUnit walks the hierarchy
    /// base-first — so fixtures are free to register users and seed data from their own one-time
    /// setup without the client being null underneath them.
    /// </summary>
    [OneTimeSetUp]
    public void InitialiseFixtureClient()
    {
        _interceptor = new DefaultHeaderInterceptor();
        IonClient    = IonClient.Create(HttpClient, WsFactory);
        IonClient.WithInterceptor(_interceptor);

        FakedTestCreds = GenerateCredentials();
    }

    [SetUp]
    public void SetupTestIdentity()
    {
        // Clear authorisation between tests: leftover credentials from the previous test in this
        // fixture are the classic source of "passes alone, fails in a run" flakiness.
        _interceptor.SetToken(null);
        FakedTestCreds = GenerateCredentials();
    }

    /// <summary>
    /// Builds credentials guaranteed unique across parallel fixtures and repeated runs against a
    /// reused container: a timestamp alone collides when two fixtures register in the same
    /// millisecond, so a random suffix goes in as well.
    /// </summary>
    protected static NewUserCredentialsInputForTest GenerateCredentials()
    {
        var unique = $"{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}_{Guid.NewGuid():N}"[..24];

        return new Faker<NewUserCredentialsInputForTest>("en")
           .RuleFor(u => u.displayName, f => f.Internet.UserName())
           .RuleFor(u => u.username, f => $"{f.Random.AlphaNumeric(6)}_{unique}")
           .RuleFor(u => u.email, f => $"{f.Random.AlphaNumeric(6)}_{unique}@test.local")
           .RuleFor(u => u.argreeTos, _ => true)
           .RuleFor(u => u.argreeOptionalEmails, _ => true)
           .RuleFor(u => u.birthDate, f => f.Date.BetweenDateOnly(new DateOnly(1995, 1, 1), new DateOnly(2000, 1, 1)))
           .RuleFor(u => u.password, f => f.Internet.Password(8, false, "\\w", "Aa1!"))
           .Generate();
    }

    private Task<WebSocket> WsFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = FactoryAsp.Server.CreateWebSocketClient();
        protocols ??= [];
        foreach (var protocol in protocols) socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    }

    /// <summary>
    /// Registers a new user and returns the token. Also updates FakedTestCreds with the new user's credentials.
    /// </summary>
    protected async Task<string> RegisterAndGetTokenAsync(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var creds = GenerateCredentials();

        // Tests that follow up on the registration (password reset, login) read the credentials back
        // off the fixture, so the ambient set has to track the user we just created.
        FakedTestCreds = creds;

        var result = await IonClient.ForService<IIdentityInteraction>(scope.ServiceProvider).Registration(
            new NewUserCredentialsInput(
                creds.email,
                creds.username,
                creds.password,
                creds.displayName,
                creds.argreeTos,
                creds.birthDate,
                creds.argreeOptionalEmails,
                creds.captchaToken,
                "1.0",
                "1.0"),
            ct);

        if (result is not SuccessRegistration sr)
        {
            var err = result as FailedRegistration;
            Assert.Fail($"Registration failed: {err!.error} - Field: {err.field} - Message: {err.message}");
            return string.Empty;
        }

        return sr.token;
    }

    /// <summary>
    /// Registers a user and returns an isolated client already authenticated as them. Use this when
    /// a test needs more than one identity at a time — two sessions never share a token, so the
    /// ambient <see cref="SetAuthToken"/> state cannot leak between them.
    /// </summary>
    protected async Task<TestUserSession> CreateSessionAsync(CancellationToken ct = default)
    {
        var interceptor = new DefaultHeaderInterceptor();
        var client      = IonClient.Create(HttpClient, WsFactory);
        client.WithInterceptor(interceptor);

        var creds = GenerateCredentials();

        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var result = await client.ForService<IIdentityInteraction>(scope.ServiceProvider).Registration(
            new NewUserCredentialsInput(
                creds.email,
                creds.username,
                creds.password,
                creds.displayName,
                creds.argreeTos,
                creds.birthDate,
                creds.argreeOptionalEmails,
                creds.captchaToken,
                "1.0",
                "1.0"),
            ct);

        if (result is not SuccessRegistration sr)
        {
            var err = result as FailedRegistration;
            Assert.Fail($"Registration failed: {err!.error} - Field: {err.field} - Message: {err.message}");
            return null!;
        }

        interceptor.SetToken(sr.token);

        var session = new TestUserSession(client, FactoryAsp.Services, creds, sr.token);
        session.UserId = (await session.Users.GetMe(ct)).userId;
        return session;
    }

    /// <summary>
    /// An unauthenticated client with a machine identity of its own — a browser sitting on the
    /// sign-in screen.
    /// </summary>
    /// <remarks>
    /// QR login is the one flow that needs both sides at once and needs them to be <em>different
    /// machines</em>: the desktop asking for a code has no token, the phone approving it has one,
    /// and the whole security argument rests on the two not being the same device. Acting the
    /// desktop with the ambient client would make the machine ids match and quietly pass a test
    /// that should have caught a missing check.
    /// </remarks>
    protected TestBrowser CreateBrowser()
    {
        var interceptor = new DefaultHeaderInterceptor();
        var client      = IonClient.Create(HttpClient, WsFactory);

        client.WithInterceptor(interceptor);

        return new TestBrowser(client, FactoryAsp.Services);
    }

    protected void SetAuthToken(string token)
        => _interceptor.SetToken(token);

    /// <summary>
    /// Drops the ambient bearer token so the next call goes out unauthenticated. Tests that switch
    /// identity mid-test use this between users; it makes "who am I right now" explicit instead of
    /// leaving the previous user's token in place until the new one happens to overwrite it.
    /// </summary>
    protected void ResetAuthentication()
        => _interceptor.SetToken(null);

    protected IIdentityInteraction GetIdentityService(IServiceProvider? serviceProvider = null)
        => IonClient.ForService<IIdentityInteraction>(serviceProvider ?? FactoryAsp.Services);

    protected IUserInteraction GetUserService(IServiceProvider? serviceProvider = null)
        => IonClient.ForService<IUserInteraction>(serviceProvider ?? FactoryAsp.Services);

    protected IServerInteraction GetServerService(IServiceProvider? serviceProvider = null)
        => IonClient.ForService<IServerInteraction>(serviceProvider ?? FactoryAsp.Services);

    protected IChannelInteraction GetChannelService(IServiceProvider? serviceProvider = null)
        => IonClient.ForService<IChannelInteraction>(serviceProvider ?? FactoryAsp.Services);

    protected IInventoryInteraction GetInventoryService(IServiceProvider? serviceProvider = null)
        => IonClient.ForService<IInventoryInteraction>(serviceProvider ?? FactoryAsp.Services);

    protected ISecurityInteraction GetSecurityService(IServiceProvider? serviceProvider = null)
        => IonClient.ForService<ISecurityInteraction>(serviceProvider ?? FactoryAsp.Services);

    protected IUltimaInteraction GetUltimaService(IServiceProvider? serviceProvider = null)
        => IonClient.ForService<IUltimaInteraction>(serviceProvider ?? FactoryAsp.Services);

    protected IFriendsInteraction GetFriendsService(IServiceProvider? serviceProvider = null)
        => IonClient.ForService<IFriendsInteraction>(serviceProvider ?? FactoryAsp.Services);

    protected IPrivacyInteraction GetPrivacyService(IServiceProvider? serviceProvider = null)
        => IonClient.ForService<IPrivacyInteraction>(serviceProvider ?? FactoryAsp.Services);

    protected FakeXsollaService GetFakeXsolla()
        => FactoryAsp.Services.GetRequiredService<FakeXsollaService>();

    protected IGrainFactory GetGrainFactory()
        => FactoryAsp.Services.GetRequiredService<IGrainFactory>();

    protected ITestCodeStore GetTestCodeStore()
        => FactoryAsp.Services.GetRequiredService<ITestCodeStore>();

    protected async Task<string?> GetEmailCodeAsync(string email, TimeSpan? timeout = null, CancellationToken ct = default)
        => await GetTestCodeStore().GetCodeAsync(email, TestCodeType.Email, timeout ?? TimeSpan.FromSeconds(5), ct);

    protected async Task<string?> GetPhoneCodeAsync(string phone, TimeSpan? timeout = null, CancellationToken ct = default)
        => await GetTestCodeStore().GetCodeAsync(phone, TestCodeType.Phone, timeout ?? TimeSpan.FromSeconds(5), ct);

    protected async Task<Guid> CreateSpaceAndGetIdAsync(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var result = await GetUserService(scope.ServiceProvider).CreateSpace(
            new CreateServerRequest("Test Space", "Description", string.Empty),
            ct);

        if (result is not SuccessCreateSpace success)
        {
            var failed = result as FailedCreateSpace;
            Assert.Fail($"Failed to create space: {failed!.error}");
            return Guid.Empty;
        }

        return success.space.spaceId;
    }

    protected async Task<Guid> CreateTextChannelAsync(Guid spaceId, string channelName = "test-channel", CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        await GetChannelService(scope.ServiceProvider).CreateChannel(
            spaceId,
            Guid.Empty, // не используется в реализации
            new CreateChannelRequest(spaceId, channelName, ChannelType.Text, "Test channel description", null),
            ct);

        // Канал создан, нужно получить его ID из БД
        // Получаем текущего пользователя
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        // Устанавливаем RequestContext для grain вызова
        Orleans.Runtime.RequestContext.Set("$caller_user_id", user.userId);

        try
        {
            var spaceGrain = FactoryAsp.Services.GetRequiredService<IGrainFactory>()
               .GetGrain<ISpaceGrain>(spaceId);

            var channels = await spaceGrain.GetChannels();
            var createdChannel = channels.FirstOrDefault(c => c.channel.name == channelName);

            if (createdChannel == null)
                Assert.Fail($"Failed to find created channel '{channelName}'");

            return createdChannel!.channel.channelId;
        }
        finally
        {
            Orleans.Runtime.RequestContext.Clear();
        }
    }
}

/// <summary>
/// A client that has never signed in, with its own machine identity. See
/// <see cref="TestBase.CreateBrowser"/> for why QR login needs one.
/// </summary>
public sealed class TestBrowser(IonClient client, IServiceProvider services)
{
    public IonClient Client { get; } = client;

    public IIdentityInteraction Identity => Client.ForService<IIdentityInteraction>(services);
}

/// <summary>
/// A registered user plus a client bound to their token. Handing tests a session instead of mutating
/// one ambient token is what makes multi-user scenarios (friends, permissions, blocking) expressible
/// without the two identities fighting over the same interceptor.
/// </summary>
public sealed class TestUserSession(
    IonClient client,
    IServiceProvider services,
    NewUserCredentialsInputForTest credentials,
    string token)
{
    public IonClient                      Client      { get; } = client;
    public NewUserCredentialsInputForTest Credentials { get; } = credentials;
    public string                         Token       { get; } = token;
    public Guid                           UserId      { get; internal set; }

    public IUserInteraction      Users     => Client.ForService<IUserInteraction>(services);
    public IServerInteraction    Servers   => Client.ForService<IServerInteraction>(services);
    public IChannelInteraction   Channels  => Client.ForService<IChannelInteraction>(services);
    public IFriendsInteraction   Friends   => Client.ForService<IFriendsInteraction>(services);
    public IIdentityInteraction  Identity  => Client.ForService<IIdentityInteraction>(services);
    public ISecurityInteraction  Security  => Client.ForService<ISecurityInteraction>(services);
    public IInventoryInteraction Inventory => Client.ForService<IInventoryInteraction>(services);
    public IUltimaInteraction    Ultima    => Client.ForService<IUltimaInteraction>(services);
    public IPrivacyInteraction   Privacy   => Client.ForService<IPrivacyInteraction>(services);
}
