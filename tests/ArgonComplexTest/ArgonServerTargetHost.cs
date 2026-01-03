namespace ArgonComplexTest;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

public class ArgonServerTargetHost(string redisConnectionString, string natsConnectionString, string cockroachConnectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices((context, services) => 
        { 
            services.AddKeyedSingleton<DefaultHeaderInterceptor>(nameof(DefaultHeaderInterceptor));
        });
        
        builder.UseSetting("ConnectionStrings:cache", redisConnectionString);
        builder.UseSetting("ConnectionStrings:nats", natsConnectionString);
        builder.UseSetting("ConnectionStrings:Default", cockroachConnectionString);
        
        builder.UseSetting("CallKit:Sfu:CommandUrl", "http://localhost:7880");
        builder.UseSetting("CallKit:Sfu:ClientId", "test-api-key");
        builder.UseSetting("CallKit:Sfu:Secret", "test-secret-key-that-is-long-enough-to-be-256-bits-minimum-for-livekit");
    }
}