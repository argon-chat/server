namespace Argon.Api.Features.MediaStorage;

using Storages;
using Genbox.SimpleS3.Core.Abstracts.Clients;
using Genbox.SimpleS3.Core.Common.Authentication;
using Genbox.SimpleS3.Core.Extensions;
using Genbox.SimpleS3.Extensions.GenericS3.Extensions;

public static class CdnFeatureExtensions
{
    public static IServiceCollection AddContentDeliveryNetwork(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<CdnOptions>(builder.Configuration.GetSection("Cdn"));

        var opt           = builder.Configuration.GetSection("Cdn:Storage");
        var options       = builder.Services.Configure<StorageOptions>(opt);
        var bucketOptions = new StorageOptions();
        opt.Bind(bucketOptions);



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
        if (keyName == StorageKind.GenericS3)
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
        var coreBuilder      = SimpleS3CoreServices.AddSimpleS3Core(builder.Services);

        coreBuilder.UseGenericS3(config =>
        {
            config.Endpoint    = options.BaseUrl;
            config.RegionCode  = options.Region;
            config.Credentials = new StringAccessKey(options.Login, options.Password);
        });

        var storageServices = storageContainer.BuildServiceProvider();

        builder.Services.AddKeyedSingleton<IServiceProvider>($"{keyName}:container", storageServices);
        builder.Services.AddKeyedSingleton<IObjectClient>($"{keyName}:client", (services, o)
            => services.GetRequiredKeyedService<IServiceProvider>($"{keyName}:container").GetRequiredService<IObjectClient>());

        return builder.Services;
    }
}