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
    private readonly IObjectClient _client;
    private readonly ServiceProvider _provider;
    private bool _disposed;

    public S3ClientPool(IOptions<StorageOptions> options)
    {
        var opts = options.Value;
        var services = new ServiceCollection();

        var coreBuilder = SimpleS3CoreServices.AddSimpleS3Core(services);
        coreBuilder.UseHttpClient();
        coreBuilder.UseGenericS3(config =>
        {
            config.Endpoint        = opts.UseSsl ? $"https://{opts.Endpoint}" : $"http://{opts.Endpoint}";
            config.RegionCode      = opts.Region;
            config.Credentials     = new StringAccessKey(opts.AccessKey, opts.SecretKey);
            config.NamingMode      = NamingMode.PathStyle;
            config.PayloadSignatureMode = SignatureMode.FullSignature;
        });

        _provider = services.BuildServiceProvider();
        _client   = _provider.GetRequiredService<IObjectClient>();
    }

    public IObjectClient GetClient() => _client;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _provider.Dispose();
    }
}
