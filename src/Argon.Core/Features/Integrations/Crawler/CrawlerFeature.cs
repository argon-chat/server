namespace Argon.Features.Integrations.Crawler;

public static class CrawlerFeature
{
    public static IServiceCollection AddCrawlerFeature(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<CrawlerOptions>(builder.Configuration.GetSection(CrawlerOptions.SectionName));
        builder.Services.AddSingleton<ICrawlerService, NatsCrawlerService>();
        return builder.Services;
    }
}
