namespace Argon.Features.Storage;

using Microsoft.Extensions.DependencyInjection;

public static class StorageFeature
{
    public static void AddFileStorageFeature(this WebApplicationBuilder builder)
    {

        builder.Services.AddSingleton<IS3ClientPool, S3ClientPool>();
        builder.Services.AddSingleton<IS3StorageService, S3StorageService>();
        builder.Services.AddSingleton<S3PresignedUrlGenerator>();
        builder.Services.AddSingleton<IExportS3Service, ExportS3Service>();
        builder.Services.AddScoped<IReferenceCountService, ReferenceCountService>();
    }
}
