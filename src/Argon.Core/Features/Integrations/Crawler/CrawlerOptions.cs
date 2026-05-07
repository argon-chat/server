namespace Argon.Features.Integrations.Crawler;

public class CrawlerOptions
{
    public const string SectionName = "Crawler";

    public string SubjectPrefix { get; set; } = "argon.crawler";
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
}
