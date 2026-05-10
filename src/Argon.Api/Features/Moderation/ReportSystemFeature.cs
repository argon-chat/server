namespace Argon.Features.Moderation;

using Argon.Features.Env;

public static class ReportSystemFeature
{
    public static void AddReportSystem(this WebApplicationBuilder builder)
    {
        if (!builder.IsWorkerRole() && !builder.IsHybridRole())
            return;

        builder.Services.Configure<ReportSystemOptions>(
            builder.Configuration.GetSection(ReportSystemOptions.SectionName));
        builder.Services.Configure<TrustScoringOptions>(
            builder.Configuration.GetSection(TrustScoringOptions.SectionName));

        builder.Services.AddSingleton<IValidateOptions<ReportSystemOptions>, ReportSystemOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<TrustScoringOptions>, TrustScoringOptionsValidator>();

        builder.Services.AddOptionsWithValidateOnStart<ReportSystemOptions>();
        builder.Services.AddOptionsWithValidateOnStart<TrustScoringOptions>();
    }
}
