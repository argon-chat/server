namespace Argon.Features.Moderation;

using Microsoft.Extensions.DependencyInjection;

public static class ContentModerationFeature
{
    public static void AddContentModeration(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection(ModeratorConfig.SectionName);
        builder.Services.Configure<ModeratorConfig>(section);

        var config = section.Get<ModeratorConfig>();

        if (config is not { ClassLabels.Length: > 0, PrimaryModel.ModelPath.Length: > 0 })
        {
            builder.Services.AddSingleton<IContentModerationService, NoOpContentModerationService>();
            Serilog.Log.Warning("Content moderation disabled: no configuration found in {Section}", ModeratorConfig.SectionName);
            return;
        }

        if (!File.Exists(config.PrimaryModel.ModelPath))
        {
            builder.Services.AddSingleton<IContentModerationService, NoOpContentModerationService>();
            Serilog.Log.Warning("Content moderation disabled: primary model not found at {Path}", config.PrimaryModel.ModelPath);
            return;
        }

        var moderator = new ContentModerator(config);
        builder.Services.AddSingleton(moderator);
        builder.Services.AddSingleton<IContentModerationService>(sp =>
            new ContentModerationService(
                sp.GetRequiredService<ContentModerator>(),
                sp.GetRequiredService<Storage.IS3StorageService>(),
                sp.GetRequiredService<ILogger<ContentModerationService>>()));

        Serilog.Log.Information(
            "Content moderation enabled: primary={PrimaryModel}, secondary={SecondaryModel}, labels={Labels}",
            config.PrimaryModel.ModelPath,
            config.SecondaryModel?.ModelPath ?? "(none)",
            string.Join(", ", config.ClassLabels));
    }
}
