namespace Argon.Api.Features.EmailForms;

using System.Collections.Concurrent;

public static class JwtFeature
{
    public static IServiceCollection AddEMailForms(this WebApplicationBuilder builder)
    {
        builder.Services.AddHostedService<EMailFormLoader>();
        builder.Services.AddSingleton<EMailFormStorage>();
        return builder.Services;
    }
}

public class EMailFormStorage
{
    private readonly ConcurrentDictionary<string, string> htmlForms = new();

    public void Load(string name, string content) => htmlForms.TryAdd(name, content);

    public string GetContentFor(string formKey)
    {
        if (htmlForms.TryGetValue(formKey, out var form))
            return form;
        throw new InvalidOperationException($"No '{formKey}' form found");
    }

    public string CompileAndGetForm(string formKey, Dictionary<string, string> values)
    {
        var form = GetContentFor(formKey);

        foreach (var (key, value) in values)
            form = form.Replace($"{{{{{key.ToLowerInvariant()}}}}}", value);
        return form;
    }
}

public class EMailFormLoader(EMailFormStorage storage, ILogger<EMailFormLoader> logger) : BackgroundService
{
    protected async override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var formFiles = Directory.EnumerateFiles("./Resources", "*.html").ToList();

        logger.LogInformation("Found '{count}' email forms", formFiles.Count);

        foreach (var file in formFiles)
        {
            var content = await File.ReadAllTextAsync(file, stoppingToken);
            var name    = Path.GetFileNameWithoutExtension(file);
            storage.Load(name, content);

            logger.LogInformation("Loaded '{name}' email form", name);
        }
    }
}