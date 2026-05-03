namespace Argon.Features.Storage;

using Microsoft.Extensions.DependencyInjection;

public static class StorageFeature
{
    public static void AddFileStorageFeature(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<StorageOptions>(builder.Configuration.GetSection(StorageOptions.SectionName));
        builder.Services.Configure<FileLimitsOptions>(builder.Configuration.GetSection(FileLimitsOptions.SectionName));

        builder.Services.AddSingleton<IS3ClientPool, S3ClientPool>();
        builder.Services.AddSingleton<IS3StorageService, S3StorageService>();
        builder.Services.AddSingleton<S3PresignedUrlGenerator>();
        builder.Services.AddScoped<IReferenceCountService, ReferenceCountService>();
    }
}
