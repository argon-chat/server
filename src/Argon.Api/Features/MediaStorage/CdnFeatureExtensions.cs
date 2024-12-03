namespace Argon.Features.MediaStorage;

using Storages;
using Genbox.SimpleS3.Core.Abstracts.Clients;
using Genbox.SimpleS3.Core.Abstracts.Enums;
using Genbox.SimpleS3.Core.Common.Authentication;
using Genbox.SimpleS3.Core.Extensions;
using Genbox.SimpleS3.Extensions.GenericS3.Extensions;
using Genbox.SimpleS3.Extensions.HttpClient.Extensions;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.StaticFiles;

public static class CdnFeatureExtensions
{
    public static IServiceCollection AddContentDeliveryNetwork(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<CdnOptions>(builder.Configuration.GetSection("Cdn"));

        var opt           = builder.Configuration.GetSection("Cdn:Storage");
        var options       = builder.Services.Configure<StorageOptions>(opt);
        var bucketOptions = new StorageOptions();
        opt.Bind(bucketOptions);

        builder.Services.TryAddSingleton<IContentTypeProvider, FileExtensionContentTypeProvider>();

        return bucketOptions.Kind switch
        {
            StorageKind.InMemory  => throw new InvalidOperationException(),
            StorageKind.Disk      => builder.AddContentDeliveryNetwork<DiskContentDeliveryNetwork>(bucketOptions.Kind, bucketOptions),
            StorageKind.GenericS3 => builder.AddContentDeliveryNetwork<YandexContentDeliveryNetwork>(bucketOptions.Kind, bucketOptions),
            _                     => throw new ArgumentOutOfRangeException()
        };
    }

    public static IServiceCollection AddContentDeliveryNetwork<T>(this WebApplicationBuilder builder, StorageKind keyName, StorageOptions options)
        where T : class, IContentDeliveryNetwork
    {
        if (keyName == StorageKind.Disk)
        {
            builder.Services.AddKeyedSingleton<IContentStorage, DiskContentStorage>(IContentStorage.DiskContentStorageKey);
            builder.Services.AddSingleton<IContentDeliveryNetwork, T>();
        }
        else if (keyName == StorageKind.GenericS3)
        {
            builder.Services.AddKeyedSingleton<IContentStorage, S3ContentStorage>(IContentStorage.GenericS3StorageKey);
            builder.Services.AddSingleton<IContentDeliveryNetwork, T>();
            builder.AddS3Storage(keyName, options);
        }

        return builder.Services;
    }


    public static IServiceCollection AddS3Storage(this WebApplicationBuilder builder, StorageKind keyName, StorageOptions options)
    {
        var storageContainer = new ServiceCollection();
        var coreBuilder      = SimpleS3CoreServices.AddSimpleS3Core(storageContainer);
        coreBuilder.UseHttpClient();
        coreBuilder.UseGenericS3(config =>
        {
            config.Endpoint    = options.BaseUrl;
            config.RegionCode  = options.Region;
            config.Credentials = new StringAccessKey(options.Login, options.Password);
            config.NamingMode  = NamingMode.PathStyle;
        });

        var storageServices = storageContainer.BuildServiceProvider();

        builder.Services.AddKeyedSingleton<IServiceProvider>($"{keyName}:container", storageServices);
        builder.Services.AddKeyedSingleton<IObjectClient>($"{keyName}:client", (services, o)
            => storageServices.GetRequiredService<IObjectClient>());

        return builder.Services;
    }
}