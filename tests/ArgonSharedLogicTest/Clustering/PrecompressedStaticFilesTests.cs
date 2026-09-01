namespace ArgonSharedLogicTest.Clustering;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Argon.Features.Middlewares;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

/// <summary>
/// Whether the sign-in widget arrives compressed, and whether it arrives at all.
/// </summary>
/// <remarks>
/// <para>Nothing in front of this server compresses anything — Kestrel has no response compression
/// configured and the proxy adds none — so the widget's build writes brotli and gzip siblings and
/// the identity server picks between them. That rewrite is the kind of thing that works in every
/// obvious case and then serves a browser a body it cannot decode, so the cases here are the ones
/// that would produce a broken page rather than a large one.</para>
///
/// <para>Run against a real Kestrel host over a loopback socket rather than a test double: the
/// failure this guards against lives in how <c>UseStaticFiles</c> reacts to a path it was not
/// asked for, and that is exactly what a double would stub out.</para>
/// </remarks>
[TestFixture]
public class PrecompressedStaticFilesTests
{
    private static readonly byte[] Script = Encoding.UTF8.GetBytes(
        string.Concat(Enumerable.Repeat("export const widget = 'sign-in';\n", 200)));

    private string          root    = null!;
    private WebApplication  app     = null!;
    private HttpClient      client  = null!;

    [OneTimeSetUp]
    public async Task StartHost()
    {
        root = Directory.CreateTempSubdirectory("aegis-precompressed").FullName;
        Directory.CreateDirectory(Path.Combine(root, "assets"));

        var script = Path.Combine(root, "assets", "index-a1b2c3.js");
        await File.WriteAllBytesAsync(script, Script);
        await File.WriteAllBytesAsync(script + ".br", Compress(Script, brotli: true));
        await File.WriteAllBytesAsync(script + ".gz", Compress(Script, brotli: false));

        // No sibling of any kind — the shape a file added to the image by hand would have.
        await File.WriteAllTextAsync(Path.Combine(root, "assets", "plain-d4e5f6.css"), ".widget{color:red}");

        var builder = WebApplication.CreateBuilder();

        // Argon.Api's appsettings.json is next to the test assembly, and its Kestrel section names
        // TLS certificates that exist only in the container. Replaced with an empty in-memory
        // source rather than simply cleared, because UseUrls below writes a setting back into it.
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection();

        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        app = builder.Build();

        var files = new PhysicalFileProvider(root);

        app.UsePrecompressedStaticFiles(files);
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider          = files,
            ServeUnknownFileTypes = false,
            ContentTypeProvider   = new PrecompressedContentTypeProvider()
        });

        await app.StartAsync();

        var address = app.Services.GetRequiredService<IServer>().Features
           .Get<IServerAddressesFeature>()!.Addresses.First();

        // Decompression off: the point is to see the bytes and the headers as they left the server.
        client = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.None })
        {
            BaseAddress = new Uri(address)
        };
    }

    [OneTimeTearDown]
    public async Task StopHost()
    {
        client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    private static byte[] Compress(byte[] input, bool brotli)
    {
        using var output = new MemoryStream();
        using (Stream stream = brotli
                   ? new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true)
                   : new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            stream.Write(input);

        return output.ToArray();
    }

    private async Task<HttpResponseMessage> Get(string path, params string[] acceptEncoding)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);

        foreach (var value in acceptEncoding)
            request.Headers.TryAddWithoutValidation("Accept-Encoding", value);

        return await client.SendAsync(request);
    }

    [Test]
    public async Task Brotli_is_served_with_the_uncompressed_file_s_content_type()
    {
        var response = await Get("/assets/index-a1b2c3.js", "br, gzip");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "br" }));

        // The header pair a browser needs. `.br` has no type of its own, and a static-file
        // middleware that cannot name a type refuses to serve the file at all.
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/javascript"));

        var body = await response.Content.ReadAsByteArrayAsync();
        Assert.That(body.Length, Is.LessThan(Script.Length));
        Assert.That(Decompress(body, brotli: true), Is.EqualTo(Script));
    }

    /// <summary>Nobody else may see the brotli body that this caller asked for.</summary>
    [Test]
    public async Task A_compressed_answer_varies_on_accept_encoding()
    {
        var response = await Get("/assets/index-a1b2c3.js", "br");

        Assert.That(response.Headers.Vary, Does.Contain("Accept-Encoding"));
    }

    [Test]
    public async Task Gzip_is_served_when_brotli_is_not_offered()
    {
        var response = await Get("/assets/index-a1b2c3.js", "gzip, deflate");

        Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "gzip" }));
        Assert.That(Decompress(await response.Content.ReadAsByteArrayAsync(), brotli: false), Is.EqualTo(Script));
    }

    /// <summary>
    /// <c>br;q=0</c> is a refusal, and the substring test that a first attempt at this reaches for
    /// reads it as an offer — then answers with a body the caller has just said it cannot decode.
    /// </summary>
    [Test]
    public async Task Brotli_refused_with_q0_falls_through_to_gzip()
    {
        var response = await Get("/assets/index-a1b2c3.js", "br;q=0, gzip");

        Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "gzip" }));
    }

    /// <summary>A weighted preference is still a preference, not a refusal.</summary>
    [Test]
    public async Task Brotli_at_a_fractional_quality_is_still_offered()
    {
        var response = await Get("/assets/index-a1b2c3.js", "br;q=0.5, gzip;q=0.4");

        Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "br" }));
    }

    [Test]
    public async Task A_client_that_offers_nothing_gets_the_file_itself()
    {
        var response = await Get("/assets/index-a1b2c3.js");

        Assert.That(response.Content.Headers.ContentEncoding, Is.Empty);
        Assert.That(await response.Content.ReadAsByteArrayAsync(), Is.EqualTo(Script));
    }

    /// <summary>
    /// The rewrite is conditional on the sibling existing. A file dropped into the directory
    /// without one has to keep being served, not 404 because the negotiation assumed it was there.
    /// </summary>
    [Test]
    public async Task A_file_with_no_compressed_sibling_is_served_uncompressed()
    {
        var response = await Get("/assets/plain-d4e5f6.css", "br, gzip");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentEncoding, Is.Empty);
        Assert.That(response.Content.Headers.ContentType?.MediaType, Is.EqualTo("text/css"));
        Assert.That(await response.Content.ReadAsStringAsync(), Is.EqualTo(".widget{color:red}"));
    }

    /// <summary>
    /// The siblings are an implementation detail of the negotiation, but they are also real files
    /// in a served directory, and something will eventually ask for one by name.
    /// </summary>
    /// <remarks>
    /// The first version of this failed here, and instructively: the content-type provider strips
    /// <c>.br</c> and so labels the response <c>text/javascript</c>, while the negotiation looked
    /// for a <c>.br.br</c> that does not exist and set no <c>Content-Encoding</c> — brotli bytes
    /// announced as a plain script, which is a parse error rather than a page.
    /// </remarks>
    [Test]
    public async Task Asking_for_the_sibling_directly_still_answers_as_that_encoding()
    {
        var response = await Get("/assets/index-a1b2c3.js.br", "br");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Content.Headers.ContentEncoding, Is.EqualTo(new[] { "br" }));
    }

    private static byte[] Decompress(byte[] input, bool brotli)
    {
        using var source = new MemoryStream(input);
        using var output = new MemoryStream();
        using (Stream stream = brotli ? new BrotliStream(source, CompressionMode.Decompress)
                                      : new GZipStream(source, CompressionMode.Decompress))
            stream.CopyTo(output);

        return output.ToArray();
    }
}
