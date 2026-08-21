namespace Argon.Features.Moderation;

public static class ReportSystemFeature
{
    public static void AddReportSystem(this WebApplicationBuilder builder)
    {
        var reportSection = builder.Configuration.GetSection(ReportSystemOptions.SectionName);
        var trustSection  = builder.Configuration.GetSection(TrustScoringOptions.SectionName);

        builder.Services.Configure<ReportSystemOptions>(reportSection);
        builder.Services.Configure<TrustScoringOptions>(trustSection);

        builder.Services.AddSingleton<IValidateOptions<ReportSystemOptions>, ReportSystemOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<TrustScoringOptions>, TrustScoringOptionsValidator>();

        builder.Services.AddOptionsWithValidateOnStart<ReportSystemOptions>();
        builder.Services.AddOptionsWithValidateOnStart<TrustScoringOptions>();
    }
}
