namespace Argon.Features.Template;

using Fluid;

public static class TemplateFeature
{
    public static IServiceCollection AddTemplateEngine(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<SmtpConfig>(builder.Configuration.GetSection("Smtp"));
        builder.Services.AddSingleton<FluidParser>();
        builder.Services.AddHostedService<EMailFormLoader>();
        builder.Services.AddSingleton<EMailFormStorage>();
        return builder.Services;
    }
}

public class EMailFormStorage
{
    private readonly ConcurrentDictionary<string, IFluidTemplate> htmlForms = new();

    public void Load(string name, IFluidTemplate template) => htmlForms.TryAdd(name, template);

    public IFluidTemplate GetContentFor(string formKey)
    {
        if (htmlForms.TryGetValue(formKey, out var form))
            return form;
        throw new InvalidOperationException($"No '{formKey}' form found");
    }

    public string Render(string formKey, Dictionary<string, string> values)
    {
        var template = GetContentFor(formKey);

        var context = new TemplateContext();

        foreach (var (key, value) in values)
            context.SetValue(key, value);

        return template.Render(context);
    }
}

public class EMailFormLoader(EMailFormStorage storage, ILogger<EMailFormLoader> logger, FluidParser engine) : BackgroundService
{
    private bool IsLoaded = false;
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (IsLoaded) return;
        var formFiles = Directory.EnumerateFiles("./Resources", "*.html").ToList();

        logger.LogInformation("Found '{count}' email forms", formFiles.Count);

        foreach (var file in formFiles)
        {
            var content = await File.ReadAllTextAsync(file, stoppingToken);
            var name    = Path.GetFileNameWithoutExtension(file);

            if (engine.TryParse(content, out var result, out var error))
            {
                storage.Load(name, result);
                logger.LogInformation("Loaded '{name}' email form", name);
            }
            else
                logger.LogError("Failed load '{name}' email form, error: {error}", name, error);
        }

        IsLoaded = true;
    }

    public async Task ForceLoad()
        => await ExecuteAsync(CancellationToken.None);
}