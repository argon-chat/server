namespace ArgonComplexTest.Infrastructure.Account;

using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;

/// <summary>
/// Fetching a finished export archive from the object store and reading what is in it.
/// </summary>
/// <remarks>
/// <para>The download url the grain hands out is a real presigned GET against the real store — the
/// suite configures <c>Storage:*</c> rather than substituting a fake service, so the signature, the
/// bucket addressing and the expiry are production's. That is the half of this with no other way of
/// being wrong in a way a test could see, and it is the reason the archive is fetched over HTTP here
/// instead of read out of MinIO with the server's own client.</para>
///
/// <para><b>Why the connection is redirected.</b> MinIO addresses buckets virtual-host style, so the
/// url names <c>argon-test.localhost</c>, and nothing on a test machine resolves that. The host is
/// part of what was signed, so rewriting the url would invalidate the signature and test a request
/// the server never produced. Overriding the <em>connection</em> instead — dial the mapped port on
/// loopback, send the request byte for byte as signed, <c>Host</c> header included — leaves the
/// signature untouched. This is <c>MediaUploadTests.DirectToStore</c>, on the download side.</para>
/// </remarks>
public static class ExportArchive
{
    /// <summary>
    /// Downloads the archive and returns its entries as path → text.
    /// </summary>
    /// <remarks>
    /// A dictionary of strings because everything an export writes is JSON, and every assertion worth
    /// making is either "this path is present" or "this path contains that value". Entry names are
    /// the archive's own — <c>profile.json</c>, <c>dm/{id}.json</c>, <c>channels/{space}/{channel}.json</c>
    /// — with forward slashes, which is what the grain wrote them as.
    /// </remarks>
    public static async Task<IReadOnlyDictionary<string, string>> DownloadAsync(
        string downloadUrl, CancellationToken ct = default)
    {
        var (status, entries, body) = await TryDownloadAsync(downloadUrl, ct);

        if (status != HttpStatusCode.OK || entries is null)
            throw new InvalidOperationException(
                $"the object store refused the presigned export url with {(int)status} {status}: {body}");

        return entries;
    }

    /// <summary>
    /// The same fetch, reporting the store's answer instead of throwing on it.
    /// </summary>
    /// <remarks>
    /// For the tests that are <em>about</em> the url no longer working — an archive past its TTL, one
    /// removed when the account was deleted. Those want the status code as the assertion, and a
    /// helper that threw would turn the thing under test into an exception message.
    /// </remarks>
    public static async Task<(HttpStatusCode Status, IReadOnlyDictionary<string, string>? Entries, string Body)>
        TryDownloadAsync(string downloadUrl, CancellationToken ct = default)
    {
        using var client   = DirectToStore();
        using var response = await client.GetAsync(downloadUrl, ct);

        if (!response.IsSuccessStatusCode)
            return (response.StatusCode, null, await response.Content.ReadAsStringAsync(ct));

        var bytes = await response.Content.ReadAsByteArrayAsync(ct);

        return (response.StatusCode, Read(bytes), string.Empty);
    }

    /// <summary>Reads a zip in memory into path → text.</summary>
    public static IReadOnlyDictionary<string, string> Read(byte[] archive)
    {
        using var stream = new MemoryStream(archive, writable: false);
        using var zip    = new ZipArchive(stream, ZipArchiveMode.Read);

        var entries = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var entry in zip.Entries)
        {
            using var content = entry.Open();
            using var reader  = new StreamReader(content, Encoding.UTF8);

            entries[entry.FullName.Replace('\\', '/')] = reader.ReadToEnd();
        }

        return entries;
    }

    /// <summary>
    /// An HTTP client that ignores DNS and dials the store's mapped port.
    /// </summary>
    /// <remarks>
    /// Copied deliberately from <c>MediaUploadTests.DirectToStore</c> rather than shared with it: the
    /// two live on opposite sides of the suite (a fixture's private helper, and the account harness)
    /// and the duplication is four lines of socket plumbing whose only requirement is that it stays
    /// exactly this — a connection override and nothing else, so the request the store sees is the
    /// one the server signed.
    /// </remarks>
    private static HttpClient DirectToStore()
    {
        var port = int.Parse(ArgonTestEnvironment.Instance.S3Endpoint.Split(':')[1]);

        return new HttpClient(new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

                await socket.ConnectAsync(IPAddress.Loopback, port, token);

                return new NetworkStream(socket, ownsSocket: true);
            }
        });
    }
}
