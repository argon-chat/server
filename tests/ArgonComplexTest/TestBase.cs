namespace ArgonComplexTest;

using Argon.Features.Env;
using ArgonContracts;
using Argon.Core.Grains.Interfaces;
using Bogus;
using ion.runtime.client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using System.Net.WebSockets;
using Argon.Grains.Interfaces;
using Testcontainers.CockroachDb;
using Testcontainers.Nats;
using Testcontainers.Redis;

public abstract class TestBase
{
    protected ArgonServerTargetHost FactoryAsp            = null!;
    protected HttpClient            HttpClient            = null!;
    protected IonClient             IonClient             = null!;
    protected RedisContainer        RedisContainer        = null!;
    protected NatsContainer         NatsContainer         = null!;
    protected CockroachDbContainer  CockroachDbContainer  = null!;

    protected NewUserCredentialsInputForTest FakedTestCreds = null!;

    [OneTimeSetUp]
    public async Task OneTimeSetup()
    {
        Environment.SetEnvironmentVariable("ARGON_MODE", nameof(ArgonEnvironmentKind.SingleInstance));
        Environment.SetEnvironmentVariable("ARGON_ROLE", nameof(ArgonRoleKind.Hybrid));

        CockroachDbContainer = new CockroachDbBuilder()
           .WithImage("cockroachdb/cockroach:latest")
           .Build();
        await CockroachDbContainer.StartAsync();

        RedisContainer = new RedisBuilder()
           .WithImage("redis:7-alpine")
           .Build();
        await RedisContainer.StartAsync();

        NatsContainer = new NatsBuilder()
           .WithImage("nats:latest")
           .Build();
        await NatsContainer.StartAsync();

        FactoryAsp = new ArgonServerTargetHost( 
            RedisContainer.GetConnectionString(), 
            NatsContainer.GetConnectionString(),
            CockroachDbContainer.GetConnectionString());

        HttpClient = FactoryAsp.CreateClient();

        IonClient = IonClient.Create(HttpClient, WsFactory);
        IonClient.WithInterceptor(FactoryAsp.Services.GetRequiredKeyedService<DefaultHeaderInterceptor>(nameof(DefaultHeaderInterceptor)));
    }

    [SetUp]
    public void Setup()
    {
        var userFaker = new Faker<NewUserCredentialsInputForTest>("en")
           .RuleFor(u => u.displayName, f => f.Internet.UserName())
           .RuleFor(u => u.username, f => f.Random.AlphaNumeric(8))
           .RuleFor(u => u.email, f => f.Internet.Email())
           .RuleFor(u => u.argreeTos, f => true)
           .RuleFor(u => u.argreeOptionalEmails, f => true)
           .RuleFor(u => u.birthDate, f => f.Date.BetweenDateOnly(new DateOnly(1995, 1, 1), new DateOnly(2000, 1, 1)))
           .RuleFor(u => u.password, f => f.Internet.Password());

        FakedTestCreds = userFaker.Generate();
        
        FactoryAsp.Services.GetRequiredKeyedService<DefaultHeaderInterceptor>(nameof(DefaultHeaderInterceptor))
            .SetToken(null!);
    }

    [OneTimeTearDown]
    public async Task OneTimeTearDown()
    {
        HttpClient?.Dispose();
        await FactoryAsp.DisposeAsync();
        await CockroachDbContainer.StopAsync();
        await CockroachDbContainer.DisposeAsync();
        await RedisContainer.StopAsync();
        await RedisContainer.DisposeAsync();
        await NatsContainer.StopAsync();
        await NatsContainer.DisposeAsync();
    }

    private Task<WebSocket> WsFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = FactoryAsp.Server.CreateWebSocketClient();
        protocols ??= [];
        foreach (var protocol in protocols) socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    }

    protected async Task<string> RegisterAndGetTokenAsync(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        
        var result = await IonClient.ForService<IIdentityInteraction>(scope.ServiceProvider).Registration(
            new NewUserCredentialsInput(
                FakedTestCreds.email,
                FakedTestCreds.username,
                FakedTestCreds.password,
                FakedTestCreds.displayName,
                FakedTestCreds.argreeTos,
                FakedTestCreds.birthDate,
                FakedTestCreds.argreeOptionalEmails,
                FakedTestCreds.captchaToken),
            ct);

        if (result is not SuccessRegistration sr)
        {
            var err = result as FailedRegistration;
            Assert.Fail($"Registration failed: {err!.error} - Field: {err.field} - Message: {err.message}");
            return string.Empty;
        }

        return sr.token;
    }

    protected void SetAuthToken(string token)
    {
        FactoryAsp.Services.GetRequiredKeyedService<DefaultHeaderInterceptor>(nameof(DefaultHeaderInterceptor))
            .SetToken(token);
    }

    protected IIdentityInteraction GetIdentityService(IServiceProvider? serviceProvider = null)
    {
        var provider = serviceProvider ?? FactoryAsp.Services;
        return IonClient.ForService<IIdentityInteraction>(provider);
    }

    protected IUserInteraction GetUserService(IServiceProvider? serviceProvider = null)
    {
        var provider = serviceProvider ?? FactoryAsp.Services;
        return IonClient.ForService<IUserInteraction>(provider);
    }

    protected IServerInteraction GetServerService(IServiceProvider? serviceProvider = null)
    {
        var provider = serviceProvider ?? FactoryAsp.Services;
        return IonClient.ForService<IServerInteraction>(provider);
    }

    protected IChannelInteraction GetChannelService(IServiceProvider? serviceProvider = null)
    {
        var provider = serviceProvider ?? FactoryAsp.Services;
        return IonClient.ForService<IChannelInteraction>(provider);
    }

    protected IInventoryInteraction GetInventoryService(IServiceProvider? serviceProvider = null)
    {
        var provider = serviceProvider ?? FactoryAsp.Services;
        return IonClient.ForService<IInventoryInteraction>(provider);
    }

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
