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
using Microsoft.Extensions.DependencyInjection;

public static class CdnFeatureExtensions
{
    public static IServiceCollection AddContentDeliveryNetwork(this WebApplicationBuilder builder)
    {
        builder.Services.Configure<CdnOptions>(builder.Configuration.GetSection("Cdn"));


        builder.Services.TryAddSingleton<IContentTypeProvider, FileExtensionContentTypeProvider>();

        builder.AddContentDeliveryNetwork<DiskContentDeliveryNetwork>();
        builder.AddContentDeliveryNetwork<YandexContentDeliveryNetwork>();

        return builder.Services;
    }
    
    public static IServiceCollection AddContentDeliveryNetwork<T>(this WebApplicationBuilder builder)
        where T : class, IContentDeliveryNetwork
    {
        builder.Services.AddKeyedSingleton<IContentStorage, S3ContentStorage>(IContentStorage.GenericS3StorageKey);
        builder.Services.AddKeyedSingleton<IContentStorage, DiskContentStorage>(IContentStorage.DiskContentStorageKey);
        builder.Services.AddSingleton<IContentDeliveryNetwork, T>();
        builder.AddS3Storage();
        return builder.Services;
    }


    public static IServiceCollection AddS3Storage(this WebApplicationBuilder builder)
    {
        builder.Services.AddKeyedScoped($"GenericS3:container", (provider, _) => GenerateFactoryS3Provider(provider));
        builder.Services.AddKeyedScoped($"GenericS3:client", (services, _)
            => services.GetRequiredKeyedService<IServiceProvider>("GenericS3:container").GetRequiredService<IObjectClient>());

        return builder.Services;
    }

    private static IServiceProvider GenerateFactoryS3Provider(IServiceProvider provider)
    {
        var opt              = provider.GetRequiredService<IOptions<StorageOptions>>();
        var options          = opt.Value;
        var storageContainer = new ServiceCollection();
        var coreBuilder      = SimpleS3CoreServices.AddSimpleS3Core(storageContainer);
        coreBuilder.UseHttpClient();
        coreBuilder.UseGenericS3(config => {
            config.Endpoint             = options.BaseUrl;
            config.RegionCode           = options.Region;
            config.Credentials          = new StringAccessKey(options.Login, options.Password);
            config.NamingMode           = NamingMode.PathStyle;
            config.PayloadSignatureMode = SignatureMode.FullSignature;
        });

        return storageContainer.BuildServiceProvider();
    }
}