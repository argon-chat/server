namespace Argon.Api.Http.Tests;

using Projects;

public class TestAppBuilder
{
    protected readonly DistributedApplication AppHost;
    protected readonly ResourceNotificationService ResourceNotificationService;

    protected TestAppBuilder()
    {
        var appHost = DistributedApplicationTestingBuilder.CreateAsync<Argon_Api>().Result;
        appHost.Services.ConfigureHttpClientDefaults(clientBuilder =>
        {
            clientBuilder.AddStandardResilienceHandler();
        });
        // // To output logs to the xUnit.net ITestOutputHelper, consider adding a package from https://www.nuget.org/packages?q=xunit+logging
        AppHost = appHost.BuildAsync().Result;
        ResourceNotificationService = AppHost.Services.GetRequiredService<ResourceNotificationService>();
        AppHost.StartAsync().GetAwaiter().GetResult();
    }

    ~TestAppBuilder()
    {
        AppHost.StopAsync().GetAwaiter().GetResult();
    }
}