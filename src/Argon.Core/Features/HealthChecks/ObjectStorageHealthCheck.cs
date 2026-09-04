namespace Argon.HealthChecks;

using Argon.Features.Storage;
using Microsoft.Extensions.Diagnostics.HealthChecks;

/// <summary>
/// Can this process list the buckets it stores files in?
/// </summary>
/// <remarks>
/// <para>A list of at most one key against each configured bucket. Listing rather than a head on a
/// known object because there is no known object — a fresh bucket is empty and still has to pass —
/// and rather than a bucket head because the client the storage feature builds is an object client.
/// One key is enough to exercise the endpoint, the credentials, the signing style and the bucket's
/// existence, which are the four things a deployment gets wrong.</para>
///
/// <para>The export bucket is checked alongside the main one where it is set. It is the one the jobs
/// role writes account exports to, and a deployment that names it but cannot reach it fails an
/// export hours after the pod was promoted.</para>
///
/// <para>Unconfigured is unhealthy here, not skipped: this check is registered by the storage
/// feature, so any role running it stores files, and a role that stores files with no credentials
/// throws on the first upload. Saying so at start-up is the point.</para>
/// </remarks>
public sealed class ObjectStorageHealthCheck(
    IS3ClientPool           pool,
    IOptions<StorageOptions> storage,
    IOptions<ProbeOptions>   options) : DependencyHealthCheck(options)
{
    protected override async Task<HealthCheckResult> ProbeAsync(CancellationToken ct)
    {
        var settings = storage.Value;

        if (!settings.IsConfigured)
            return HealthCheckResult.Unhealthy(
                "Storage:AccessKey and Storage:SecretKey are not set, and this role stores files");

        var buckets = new[] { settings.BucketName, settings.ExportBucketName }
           .Where(bucket => !string.IsNullOrWhiteSpace(bucket))
           .Distinct(StringComparer.Ordinal)
           .ToArray();

        if (buckets.Length == 0)
            return HealthCheckResult.Unhealthy("Storage:BucketName is not set; there is nowhere to put a file");

        var client   = pool.GetClient();
        var data     = new Dictionary<string, object> { ["endpoint"] = settings.Endpoint };
        var failures = new List<string>();

        foreach (var bucket in buckets)
        {
            var response = await client.ListObjectsAsync(bucket, request => request.MaxKeys = 1, ct);

            data[bucket] = response.StatusCode;

            if (!response.IsSuccess)
                failures.Add($"bucket '{bucket}' answered {response.StatusCode}" +
                             (response.Error is { Message.Length: > 0 } error ? $": {error.Message}" : string.Empty));
        }

        if (failures.Count > 0)
            return HealthCheckResult.Unhealthy($"{settings.Endpoint}: {string.Join("; ", failures)}", data: data);

        return HealthCheckResult.Healthy($"{settings.Endpoint} answered for {buckets.Length} bucket(s)", data);
    }
}
