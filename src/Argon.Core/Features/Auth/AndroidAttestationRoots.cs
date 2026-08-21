namespace Argon.Features.Auth;

using System.Net.Http.Json;
using System.Security.Cryptography.X509Certificates;
using Microsoft.Extensions.Options;

/// <summary>
/// Google's hardware attestation roots, fetched from Google.
/// </summary>
/// <remarks>
/// <para>Fetched rather than configured, and rather than pasted into the source. This is the trust
/// anchor for the whole attestation mechanism, Google publishes more than one and rotates them, and
/// a certificate transcribed by hand is the sort of thing that is wrong in a way nobody notices —
/// until every Android device is refused, or worse, until one is wrongly accepted. Asking the
/// publisher is the only version of this that cannot drift.</para>
///
/// <para>Configuration still wins when it is set, for an air-gapped deployment or to pin a specific
/// root deliberately. It is an override, not the normal path.</para>
///
/// <para>A failed fetch leaves the set empty, and an empty set means the verifier cannot judge — it
/// downgrades to <see cref="DeviceAssurance.KEY"/> and says so loudly rather than refusing every
/// Android device because a request to Google timed out.</para>
/// </remarks>
public sealed class AndroidAttestationRoots(
    IHttpClientFactory http,
    IOptions<AndroidAttestationOptions> options,
    ILogger<AndroidAttestationRoots> logger)
{
    /// <summary>Google's published set, as a JSON array of PEM strings.</summary>
    private const string RootsUrl = "https://android.googleapis.com/attestation/root";

    /// <summary>
    /// How long a successful fetch is trusted.
    /// </summary>
    /// <remarks>
    /// Roots rotate on the scale of years and are announced long in advance, so this is about
    /// eventually noticing a new one rather than about staying current minute to minute.
    /// </remarks>
    private static readonly TimeSpan RefreshAfter = TimeSpan.FromHours(24);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private X509Certificate2[] _roots = [];
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public async Task<X509Certificate2[]> GetAsync(CancellationToken ct = default)
    {
        // An explicit pin is the whole answer when it is present: never reach out, never refresh.
        if (options.Value.RootCertificatesPem.Length > 0)
            return Parse(options.Value.RootCertificatesPem, "configuration");

        if (_roots.Length > 0 && DateTimeOffset.UtcNow - _fetchedAt < RefreshAfter)
            return _roots;

        await _gate.WaitAsync(ct);

        try
        {
            // Re-checked inside the gate: several requests can arrive together on a cold start, and
            // only the first of them should go to Google.
            if (_roots.Length > 0 && DateTimeOffset.UtcNow - _fetchedAt < RefreshAfter)
                return _roots;

            var pem = await http.CreateClient(nameof(AndroidAttestationRoots))
               .GetFromJsonAsync<string[]>(RootsUrl, ct);

            if (pem is { Length: > 0 } && Parse(pem, "google") is { Length: > 0 } parsed)
            {
                _roots     = parsed;
                _fetchedAt = DateTimeOffset.UtcNow;

                logger.LogInformation("Loaded {Count} Android attestation root(s) from Google", parsed.Length);
            }

            return _roots;
        }
        catch (Exception e)
        {
            // The previously fetched set is kept rather than cleared: a stale root is still a real
            // root, and dropping it would turn a network blip into a fleet-wide downgrade.
            logger.LogError(e, "Could not fetch Android attestation roots; keeping {Count} cached", _roots.Length);
            return _roots;
        }
        finally
        {
            _gate.Release();
        }
    }

    private X509Certificate2[] Parse(string[] pem, string source)
    {
        var parsed = new List<X509Certificate2>(pem.Length);

        foreach (var text in pem)
        {
            try
            {
                parsed.Add(X509Certificate2.CreateFromPem(text));
            }
            catch (Exception e)
            {
                // One unreadable entry must not cost the others: a new root in a format this build
                // does not understand should not disable the ones it does.
                logger.LogError(e, "Skipping an unparseable Android attestation root from {Source}", source);
            }
        }

        return [.. parsed];
    }
}
