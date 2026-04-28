namespace ArgonComplexTest;

using Argon.Core.Features.Integrations.Xsolla;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

public class ArgonServerTargetHost(string redisConnectionString, string natsConnectionString, string cockroachConnectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseDefaultServiceProvider(options =>
        {
            // BotApiRegistration.MapBotApi resolves scoped InteractionResponsePusher from root provider;
            // disable scope validation so the test host can start
            options.ValidateScopes = false;
        });

        builder.ConfigureServices((context, services) => 
        { 
            services.AddKeyedSingleton<DefaultHeaderInterceptor>(nameof(DefaultHeaderInterceptor));
            services.AddSingleton<FakeXsollaService>();
            services.AddSingleton<IXsollaService>(sp => sp.GetRequiredService<FakeXsollaService>());
        });
        
        builder.UseSetting("ConnectionStrings:cache", redisConnectionString);
        builder.UseSetting("ConnectionStrings:nats", natsConnectionString);
        builder.UseSetting("ConnectionStrings:Default", cockroachConnectionString);
        
        builder.UseSetting("CallKit:Sfu:CommandUrl", "http://localhost:7880");
        builder.UseSetting("CallKit:Sfu:ClientId", "test-api-key");
        builder.UseSetting("CallKit:Sfu:Secret", "test-secret-key-that-is-long-enough-to-be-256-bits-minimum-for-livekit");
        
        builder.UseSetting("Xsolla:ProjectId", "1");
        builder.UseSetting("Xsolla:MerchantId", "1");
        builder.UseSetting("Xsolla:ApiKey", "test-key");
        builder.UseSetting("Xsolla:WebhookSecret", "test-secret");
        builder.UseSetting("Xsolla:IsSandbox", "true");
        builder.UseSetting("Xsolla:LoginProjectId", "00000000-0000-0000-0000-000000000001");
        builder.UseSetting("Xsolla:ServerOAuthClientId", "1");
        builder.UseSetting("Xsolla:ServerOAuthClientSecret", "test-oauth-secret");
    }
}