namespace Argon.Features.Moderation;

public static class ReportSystemFeature
{
    public static void AddReportSystem(this WebApplicationBuilder builder)
    {
        var reportSection = builder.Configuration.GetSection(ReportSystemOptions.SectionName);
        var trustSection  = builder.Configuration.GetSection(TrustScoringOptions.SectionName);

        var appSett = new FileInfo("./appsettings.json");
        var appSettProd = new FileInfo("./appsettings.Production.json");


        Console.WriteLine($"{appSett}, {appSettProd}");
        Console.WriteLine($"Exists: {appSett.Exists}, {appSettProd.Exists}");
        Console.WriteLine($"appSett: {File.ReadAllText(appSett.FullName)}");
        Console.WriteLine($"appSettProd: {File.ReadAllText(appSettProd.FullName)}");

        var log = LoggerFactory.Create(b => b.AddConsole()).CreateLogger("ReportSystem");

        var rOpts = reportSection.Get<ReportSystemOptions>();
        var tOpts = trustSection.Get<TrustScoringOptions>();

        if (rOpts is null)
            log.LogWarning("[ReportSystem] Section '{Section}' is missing or empty", ReportSystemOptions.SectionName);
        else
            log.LogInformation(
                "[ReportSystem] IsEnabled={IsEnabled}, MaxReportsPerHour={MaxReportsPerHour}, DefaultReporterCredibility={Cred}, CriticalCategories={CriticalCount}, SeriousCategories={SeriousCount}",
                rOpts.IsEnabled, rOpts.MaxReportsPerHour, rOpts.DefaultReporterCredibility,
                rOpts.CriticalCategories.Count, rOpts.SeriousCategories.Count);

        if (tOpts is null)
            log.LogWarning("[ReportSystem] Section '{Section}' is missing or empty", TrustScoringOptions.SectionName);
        else
            log.LogInformation(
                "[ReportSystem] DefaultTrustScore={Default}, MaxTrustScore={Max}, CredibilityBase={Cred}, SeverityWeights={WeightCount}, AutoActionThresholds={ThresholdCount}",
                tOpts.DefaultTrustScore, tOpts.MaxTrustScore, tOpts.CredibilityBase,
                tOpts.SeverityWeights.Count, tOpts.AutoActionThresholds.Length);

        builder.Services.Configure<ReportSystemOptions>(reportSection);
        builder.Services.Configure<TrustScoringOptions>(trustSection);

        builder.Services.AddSingleton<IValidateOptions<ReportSystemOptions>, ReportSystemOptionsValidator>();
        builder.Services.AddSingleton<IValidateOptions<TrustScoringOptions>, TrustScoringOptionsValidator>();

        builder.Services.AddOptionsWithValidateOnStart<ReportSystemOptions>();
        builder.Services.AddOptionsWithValidateOnStart<TrustScoringOptions>();
    }
}
