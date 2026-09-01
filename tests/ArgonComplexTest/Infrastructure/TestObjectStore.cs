namespace ArgonComplexTest.Infrastructure;

using Genbox.SimpleS3.Core.Abstracts.Clients;
using Genbox.SimpleS3.Core.Abstracts.Enums;
using Genbox.SimpleS3.Core.Common.Authentication;
using Genbox.SimpleS3.Core.Extensions;
using Genbox.SimpleS3.Extensions.GenericS3.Extensions;
using Genbox.SimpleS3.Extensions.HttpClient.Extensions;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Provisioning the object store the suite uploads into.
/// </summary>
/// <remarks>
/// <para>The server never creates a bucket — that is a deployment's job, and it would be an alarming
/// thing for an application holding write credentials to do on its own. So the suite has to do what
/// the deployment would, or every upload fails on a bucket that does not exist and reads as a broken
/// upload path.</para>
///
/// <para>Built on the same S3 library the server uses, and in the same path style, so that if the
/// two ever disagree about how to address a bucket this is one of the places it shows.</para>
/// </remarks>
public static class TestObjectStore
{
    public static async Task EnsureBucketAsync(
        string endpoint, string accessKey, string secretKey, string bucket, CancellationToken ct)
    {
        var services = new ServiceCollection();

        var core = SimpleS3CoreServices.AddSimpleS3Core(services);
        core.UseHttpClient();
        core.UseGenericS3(config =>
        {
            config.Endpoint             = $"http://{endpoint}";
            config.RegionCode           = "us-east-1";
            config.Credentials          = new StringAccessKey(accessKey, secretKey);
            config.NamingMode           = NamingMode.PathStyle;
            config.PayloadSignatureMode = SignatureMode.FullSignature;
        });

        await using var provider = services.BuildServiceProvider();

        var buckets = provider.GetRequiredService<IBucketClient>();

        var created = await buckets.CreateBucketAsync(bucket, token: ct);

        // A bucket that is already there is the normal case whenever containers are reused, and it is
        // not a failure — but anything else is, and swallowing it here would surface later as an
        // upload that cannot explain itself.
        if (!created.IsSuccess && created.StatusCode is not 409)
            throw new InvalidOperationException(
                $"could not create the test bucket '{bucket}' on {endpoint}: " +
                $"{created.StatusCode} {created.Error?.Message}");
    }
}
