namespace Argon.Features.Storage;

using System.Collections.Concurrent;
using Genbox.SimpleS3.Core.Abstracts.Clients;
using Genbox.SimpleS3.Core.Abstracts.Enums;
using Genbox.SimpleS3.Core.Common.Authentication;
using Genbox.SimpleS3.Core.Extensions;
using Genbox.SimpleS3.Extensions.GenericS3.Extensions;
using Genbox.SimpleS3.Extensions.HttpClient.Extensions;
using Microsoft.Extensions.DependencyInjection;

public interface IS3ClientPool : IDisposable
{
    IObjectClient GetClient();
}

public sealed class S3ClientPool : IS3ClientPool
{
    private readonly Lazy<(ServiceProvider provider, IObjectClient client)> _lazy;
    private bool _disposed;

    public S3ClientPool(IOptions<StorageOptions> options)
    {
        var opts = options.Value;
        _lazy = new Lazy<(ServiceProvider, IObjectClient)>(() =>
        {
            if (string.IsNullOrWhiteSpace(opts.AccessKey) || string.IsNullOrWhiteSpace(opts.SecretKey))
                throw new InvalidOperationException(
                    "S3 storage is not configured. Set Storage:AccessKey and Storage:SecretKey in configuration.");

            var services = new ServiceCollection();

            var coreBuilder = SimpleS3CoreServices.AddSimpleS3Core(services);
            coreBuilder.UseHttpClient();
            coreBuilder.UseGenericS3(config =>
            {
                config.Endpoint             = opts.UseSsl ? $"https://{opts.Endpoint}" : $"http://{opts.Endpoint}";
                config.RegionCode           = opts.Region;
                config.Credentials          = new StringAccessKey(opts.AccessKey, opts.SecretKey);
                config.NamingMode           = NamingMode.PathStyle;
                config.PayloadSignatureMode = SignatureMode.FullSignature;
            });

            var provider = services.BuildServiceProvider();
            var client   = provider.GetRequiredService<IObjectClient>();
            return (provider, client);
        });
    }

    public IObjectClient GetClient() => _lazy.Value.client;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_lazy.IsValueCreated)
            _lazy.Value.provider.Dispose();
    }
}
