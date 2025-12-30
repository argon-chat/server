namespace ArgonComplexTest;

using Argon.Api.Features.CoreLogic.Otp;
using Argon.Features.Env;
using ArgonContracts;
using Bogus;
using ion.runtime;
using ion.runtime.client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using System;
using System.Net.WebSockets;
using Testcontainers.Cassandra;

public class ArgonServerTargetHost(string cassandraContactPoint) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices((x, y) => { y.AddKeyedSingleton<DefaultHeaderInterceptor>(nameof(DefaultHeaderInterceptor)); });
        builder.UseSetting("Cassandra__ContactPoints__0", cassandraContactPoint);
    }
}

public sealed record NewUserCredentialsInputForTest
{
    public string   email                { get; init; }
    public string   username             { get; init; }
    public string   password             { get; init; }
    public string   displayName          { get; init; }
    public bool     argreeTos            { get; init; }
    public DateOnly birthDate            { get; init; }
    public bool     argreeOptionalEmails { get; init; }
    public string?  captchaToken         { get; init; }
}

[TestFixture, Parallelizable(ParallelScope.None)]
public class Tests
{
    private ArgonServerTargetHost factoryAsp         = null!;
    private HttpClient            httpClient         = null!;
    private IonClient             ionClient          = null!;
    private CassandraContainer    cassandraContainer = null!;

    private NewUserCredentialsInputForTest Faked_Test_Creds = null!;

    [SetUp]
    public async Task Setup()
    {
        Environment.SetEnvironmentVariable("ARGON_MODE", nameof(ArgonEnvironmentKind.SingleInstance));
        Environment.SetEnvironmentVariable("ARGON_ROLE", nameof(ArgonRoleKind.Hybrid));

        cassandraContainer = new CassandraBuilder()
           .WithImage("cassandra:5.0")
           .Build();
        await cassandraContainer.StartAsync();


        factoryAsp = new ArgonServerTargetHost(cassandraContainer.GetConnectionString());

        httpClient = factoryAsp.CreateClient();

        ionClient = IonClient.Create(httpClient, WsFactory);
        ionClient.WithInterceptor(factoryAsp.Services.GetRequiredKeyedService<DefaultHeaderInterceptor>(nameof(DefaultHeaderInterceptor)));


        var userFaker = new Faker<NewUserCredentialsInputForTest>("en")
           .RuleFor(u => u.displayName, f => f.Internet.UserName())
           .RuleFor(u => u.username, f => f.Internet.UserName())
           .RuleFor(u => u.email, f => f.Internet.Email())
           .RuleFor(u => u.argreeTos, f => true)
           .RuleFor(u => u.argreeOptionalEmails, f => true)
           .RuleFor(u => u.birthDate, f => f.Date.BetweenDateOnly(new DateOnly(1995, 1, 1), new DateOnly(2000, 1, 1)))
           .RuleFor(u => u.password, f => f.Internet.Password());

        Faked_Test_Creds = userFaker.Generate();
    }

    [TearDown]
    public async Task Down()
    {
        httpClient?.Dispose();
        await factoryAsp.DisposeAsync();
        await cassandraContainer.StopAsync();
        await cassandraContainer.DisposeAsync();
    }

    private Task<WebSocket> WsFactory(Uri uri, CancellationToken ct, string[]? protocols)
    {
        var socket = factoryAsp.Server.CreateWebSocketClient();
        protocols ??= [];
        foreach (var protocol in protocols) socket.SubProtocols.Add(protocol);
        return socket.ConnectAsync(uri, ct);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task GetAuthorizationScenario_Test(CancellationToken ct = default)
    {
        await using var scope = factoryAsp.Services.CreateAsyncScope();

        var result = await ionClient.ForService<IIdentityInteraction>(scope.ServiceProvider).GetAuthorizationScenario(ct);

        Assert.That(result, Is.EqualTo("Email_Otp"));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task Registration_Test(CancellationToken ct = default)
    {
        await using var scope = factoryAsp.Services.CreateAsyncScope();

        var result = await ionClient.ForService<IIdentityInteraction>(scope.ServiceProvider).Registration(new NewUserCredentialsInput(Faked_Test_Creds.email, Faked_Test_Creds.username, Faked_Test_Creds.password, Faked_Test_Creds.displayName, Faked_Test_Creds.argreeTos, Faked_Test_Creds.birthDate, Faked_Test_Creds.argreeOptionalEmails, Faked_Test_Creds.captchaToken), ct);

        if (result is not SuccessRegistration sr)
        {
            var err = result as FailedRegistration;
            Assert.Fail(err!.message!);
            return;
        }

        Assert.Charlie();
        //Assert.That(result, Is.EqualTo("Email_Otp"));
    }
}

public class DefaultHeaderInterceptor : IIonInterceptor
{
    private static readonly Guid    SessionId = Guid.CreateVersion7();
    private static readonly Guid    MachineId = Guid.CreateVersion7();
    private                 string? AuthToken = null;

    public async Task InvokeAsync(IIonCallContext context, Func<IIonCallContext, CancellationToken, Task> next, CancellationToken ct)
    {
        context.RequestItems.Add("Sec-Ref", SessionId.ToString());
        context.RequestItems.Add("Sec-Ner", "1");
        context.RequestItems.Add("Sec-Carry", MachineId.ToString());

        if (!string.IsNullOrEmpty(AuthToken))
        {
            context.RequestItems.Add("Authorization", $"Bearer {AuthToken}");
        }

        await next(context, ct);
    }

    public void SetToken(string t) => AuthToken = t;
}