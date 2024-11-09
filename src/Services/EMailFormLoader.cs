namespace Argon.Api.Services;

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