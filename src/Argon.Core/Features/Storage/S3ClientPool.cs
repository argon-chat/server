namespace Argon.Features.Storage;

using Genbox.SimpleS3.Core.Abstracts.Clients;
using Genbox.SimpleS3.Core.Abstracts.Enums;
using Genbox.SimpleS3.Core.Common.Authentication;
using Genbox.SimpleS3.Core.Extensions;
using Genbox.SimpleS3.Extensions.GenericS3.Extensions;
using Genbox.SimpleS3.Extensions.HttpClient.Extensions;
using Microsoft.Extensions.DependencyInjection;

public interface IS3ClientPool : IDisposable
{
    IObjectClient GetPublicClient();
    IObjectClient GetPrivateClient();
    IObjectClient GetClient(bool isPublic);
}

public sealed class S3ClientPool : IS3ClientPool
{
    private readonly Lazy<(ServiceProvider provider, IObjectClient client)> _publicLazy;
    private readonly Lazy<(ServiceProvider provider, IObjectClient client)> _privateLazy;
    private bool _disposed;

    public S3ClientPool(IOptions<StorageOptions> options)
    {
        var opts = options.Value;
        _publicLazy  = CreateLazy(opts.Public, "Public");
        _privateLazy = CreateLazy(opts.Private, "Private");
    }

    private static Lazy<(ServiceProvider, IObjectClient)> CreateLazy(S3BucketOptions bucketOpts, string name)
    {
        return new Lazy<(ServiceProvider, IObjectClient)>(() =>
        {
            if (!bucketOpts.IsConfigured)
                throw new InvalidOperationException(
                    $"S3 {name} bucket is not configured. Set Storage:{name}:AccessKey and Storage:{name}:SecretKey.");

            var services = new ServiceCollection();

            var coreBuilder = SimpleS3CoreServices.AddSimpleS3Core(services);
            coreBuilder.UseHttpClient();
            coreBuilder.UseGenericS3(config =>
            {
                config.Endpoint             = bucketOpts.UseSsl ? $"https://{bucketOpts.Endpoint}" : $"http://{bucketOpts.Endpoint}";
                config.RegionCode           = bucketOpts.Region;
                config.Credentials          = new StringAccessKey(bucketOpts.AccessKey, bucketOpts.SecretKey);
                config.NamingMode           = NamingMode.PathStyle;
                config.PayloadSignatureMode = SignatureMode.FullSignature;
            });

            var provider = services.BuildServiceProvider();
            var client   = provider.GetRequiredService<IObjectClient>();
            return (provider, client);
        });
    }

    public IObjectClient GetPublicClient()  => _publicLazy.Value.client;
    public IObjectClient GetPrivateClient() => _privateLazy.Value.client;
    public IObjectClient GetClient(bool isPublic) => isPublic ? GetPublicClient() : GetPrivateClient();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_publicLazy.IsValueCreated)  _publicLazy.Value.provider.Dispose();
        if (_privateLazy.IsValueCreated) _privateLazy.Value.provider.Dispose();
    }
}
