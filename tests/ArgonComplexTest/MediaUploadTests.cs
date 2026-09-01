namespace ArgonComplexTest;

using System.Net;
using System.Net.Sockets;
using Argon.Api.Grains.Interfaces;
using Argon.Features.Storage;
using ArgonComplexTest.Infrastructure;
using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

/// <summary>
/// Putting a file into the object store the way a client does, and getting it back out.
/// </summary>
/// <remarks>
/// <para><b>Against a real S3, because there is no other way to test this.</b> The server never
/// touches the bytes: it signs a URL, the client uploads straight to the store, and the server is
/// asked to confirm afterwards. Every interesting way that can fail — a signature the store rejects,
/// a key the server then cannot find, a size the store reports differently from what was declared —
/// happens entirely outside the process, so a substituted storage service would assert nothing but
/// the substitution.</para>
///
/// <para>Addressing is path-style by default — <c>{endpoint}/{bucket}/{key}</c> — and the last test
/// here covers the other style as well, because the host is part of what gets signed and a URL built
/// for the style a store does not speak is rejected by the store rather than by us. The client below
/// dials the container's mapped port instead of resolving the name, since nothing answers for
/// <c>argon-test.localhost</c>; the request itself is byte-for-byte the one the server signed, and
/// MinIO is told its domain is <c>localhost</c> so it reads the bucket out of a virtual-host URL
/// exactly as S3 would.</para>
/// </remarks>
[TestFixture]
public class MediaUploadTests : TestBase
{
    /// <summary>A one-pixel PNG. Small, and a real image, which matters for anything that sniffs.</summary>
    private static readonly byte[] Png = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    // ── avatars ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An avatar becomes the account's avatar only after the bytes are really in the store.
    /// </summary>
    /// <remarks>
    /// The profile is read back afterwards rather than trusting the completion call, because the
    /// interesting failure is the one where finalisation reports success and the account is left
    /// pointing at nothing.
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task An_avatar_is_attached_to_the_account_once_its_bytes_are_stored(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var users  = GetUserService(scope.ServiceProvider);
        var ticket = await Begin(() => users.BeginUploadAvatar(ct));

        await Upload(ticket, Png);
        await users.CompleteUploadAvatar(ticket.blobId, ct);

        var me = await users.GetMe(ct);

        Assert.That(me.avatarFileId, Is.Not.Null.And.Not.Empty,
            "the upload was finalized and the account still has no avatar");

        var stored = await FactoryAsp.Services.GetRequiredService<IGrainFactory>()
           .GetGrain<IFileStorageGrain>(me.userId)
           .GetFileInfoAsync(Guid.Parse(me.avatarFileId!), ct);

        Assert.Multiple(() =>
        {
            Assert.That(stored, Is.Not.Null);
            Assert.That(stored!.FileSize, Is.EqualTo(Png.Length));

            // The type the client sent, not a prefix. This asserted "image/" before the signature
            // stopped dictating a content type — a media type with no subtype, which is what every
            // avatar was stored and served as, and what a browser declines to render.
            Assert.That(stored.ContentType, Is.EqualTo("image/png"));
        });
    }

    /// <summary>
    /// Confirming an upload that never happened is refused.
    /// </summary>
    /// <remarks>
    /// This is the check that stops a client from claiming a file it did not send: the server has no
    /// other way of knowing, since it never saw the bytes. Without it an account could carry an
    /// avatar id that resolves to nothing, and every reader of that id would have to cope.
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task Confirming_an_upload_that_never_arrived_is_refused(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var users  = GetUserService(scope.ServiceProvider);
        var ticket = await Begin(() => users.BeginUploadAvatar(ct));

        // No upload at all — straight to the confirmation.
        Assert.That(async () => await users.CompleteUploadAvatar(ticket.blobId, ct), Throws.Exception,
            "a blob nobody uploaded was accepted, so the account now points at a key the store has "
          + "never heard of");
    }

    /// <summary>
    /// Something that is not an image cannot become an avatar.
    /// </summary>
    /// <remarks>
    /// Checked against what the store received rather than against what the client said it would
    /// send, because those are different claims and only the first one is evidence: the server never
    /// sees the bytes, and the declared type is discarded the moment the URL is signed.
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task An_avatar_that_is_not_an_image_is_rejected(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var users  = GetUserService(scope.ServiceProvider);
        var ticket = await Begin(() => users.BeginUploadAvatar(ct));

        await Upload(ticket, Png, contentType: "application/pdf");

        Assert.That(async () => await users.CompleteUploadAvatar(ticket.blobId, ct), Throws.Exception,
            "a document was accepted as an avatar; the purpose's content-type rule is the only thing "
          + "standing between the avatar slot and arbitrary files");

        var me = await users.GetMe(ct);

        Assert.That(me.avatarFileId, Is.Null.Or.Empty,
            "the upload was rejected and the account was left pointing at it anyway");
    }

    // ── chat media ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// An attachment in a channel comes back with the size the store actually holds.
    /// </summary>
    /// <remarks>
    /// The size is asserted against the bytes that were sent rather than against what the client
    /// declared up front, because those are two different numbers and only one of them is a fact. The
    /// server takes it from a <c>HEAD</c> against the store for exactly that reason, and a client that
    /// under-declares to get past the size limit is the case that makes it matter.
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task A_channel_attachment_is_described_by_what_the_store_holds(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, ct: ct);

        var channels = GetChannelService(scope.ServiceProvider);
        var ticket   = await Begin(() => channels.BeginUploadAttachment(spaceId, channelId, ct));

        await Upload(ticket, Png);

        var attachment = await channels.CompleteUploadAttachment(spaceId, channelId, ticket.blobId, ct);

        Assert.Multiple(() =>
        {
            Assert.That(attachment.fileId, Is.Not.EqualTo(Guid.Empty));
            Assert.That(attachment.fileSize, Is.EqualTo(Png.Length),
                "the size came from the request rather than from the store, so a client that lies "
              + "about it is believed");
        });
    }

    /// <summary>
    /// An attachment belongs to its channel, and a member of neither cannot start one.
    /// </summary>
    [Test, CancelAfter(300_000)]
    public async Task An_outsider_cannot_start_an_upload_into_someone_elses_channel(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, ct: ct);

        var outsider = await CreateSessionAsync(ct);

        IUploadFileResult? handed = null;

        try
        {
            handed = await outsider.Client
               .ForService<IChannelInteraction>(FactoryAsp.Services)
               .BeginUploadAttachment(spaceId, channelId, ct);
        }
        catch (Exception)
        {
            // Refused loudly, which is a refusal all the same. The two shapes are not worth pinning
            // down here: what this test is about is that no usable ticket comes back.
        }

        Assert.That(handed, Is.Null.Or.InstanceOf<FailedUploadFile>(),
            "a stranger was handed a signed URL for a channel they cannot see, and from that point on "
          + "the object store is the only thing between them and writing into it");
    }

    // ── limits ──────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A file declared larger than the limit is refused before anything is signed.
    /// </summary>
    /// <remarks>
    /// Refusing up front is the only cheap place to do it: once a URL is signed the client can upload
    /// whatever it likes to the store, and the server does not find out until it is already paying to
    /// keep it. Finalisation checks the real size too, but by then the bytes have been transferred.
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task A_file_over_the_limit_is_refused_before_a_url_is_signed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        SetAuthToken(await RegisterAndGetTokenAsync(ct));

        var spaceId   = await CreateSpaceAndGetIdAsync(ct);
        var channelId = await CreateTextChannelAsync(spaceId, ct: ct);

        var grain = FactoryAsp.Services.GetRequiredService<IGrainFactory>()
           .GetGrain<IFileStorageGrain>(await CurrentUserId(scope, ct));

        Assert.That(
            async () => await grain.RequestUploadAsync(
                new FileUploadRequest(FilePurpose.ChannelAttachment, "video/mp4",
                    FileSize: 512L * 1024 * 1024, spaceId, channelId), ct),
            Throws.Exception,
            "half a gigabyte was accepted for a channel attachment, and the limit only exists here");
    }

    // ── the address handed to clients ───────────────────────────────────────────────────────────

    /// <summary>
    /// The public file address redirects somewhere a caller can actually go.
    /// </summary>
    /// <remarks>
    /// <para><b>This is the shape of a production outage, not a hypothetical.</b> The endpoint reads
    /// the storage options; the role that serves it did not declare that section; the options bound to
    /// a default-constructed instance with no regional origins. The redirect then carried the bare
    /// object key as its <c>Location</c> — relative, so every caller resolved it against the API and
    /// got a 404 — and no cache window, so the round trip was paid again on every image. Nothing
    /// failed to start, nothing was logged, and every avatar in the product was broken.</para>
    ///
    /// <para>Both halves are asserted because both were wrong for the same reason and either alone
    /// would let the other back in.</para>
    /// </remarks>
    [Test, CancelAfter(300_000)]
    public async Task A_file_address_redirects_somewhere_absolute_and_says_how_long_it_keeps()
    {
        using var client = FactoryAsp.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync($"{CdnOptions.FilePath}/{Guid.CreateVersion7()}");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Found));

        var location = response.Headers.Location;

        Assert.Multiple(() =>
        {
            Assert.That(location?.IsAbsoluteUri, Is.True,
                $"the redirect is relative ('{location}'), so a browser resolves it against whatever "
              + "host it asked — which is the API, where nothing serves it");

            Assert.That(response.Headers.CacheControl?.ToString(), Does.Contain("max-age="),
                "the redirect is region-dependent but not cacheable at all, so every image fetch pays "
              + "for a round trip that could have been remembered");
        });
    }

    // ── addressing ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Both ways of addressing the store produce a URL it accepts.
    /// </summary>
    /// <remarks>
    /// <para>These two used to disagree: the server's own client spoke path-style while the URLs it
    /// handed clients were virtual-host. Against a provider that accepts both, that is invisible.
    /// Against one that accepts only path-style — which is every self-hosted store reached by address
    /// or by a name with no wildcard record — every client upload fails at the store, on a URL the
    /// server signed perfectly well, and nothing on the server side has anything to log.</para>
    ///
    /// <para>So both are exercised against a real store rather than compared as strings. The host is
    /// signed into the request, so a style the store does not expect is not a cosmetic difference: it
    /// is a signature the store computes differently and refuses.</para>
    /// </remarks>
    [TestCase(true,  TestName = "{m}(path-style)")]
    [TestCase(false, TestName = "{m}(virtual-host)")]
    [CancelAfter(300_000)]
    public async Task A_presigned_url_is_accepted_in_either_addressing_style(bool pathStyle)
    {
        var settings = FactoryAsp.Settings;

        var generator = new S3PresignedUrlGenerator(Options.Create(new StorageOptions
        {
            Endpoint     = settings.S3Endpoint,
            AccessKey    = settings.S3AccessKey,
            SecretKey    = settings.S3SecretKey,
            BucketName   = settings.S3Bucket,
            Region       = "us-east-1",
            UseSsl       = false,
            UsePathStyle = pathStyle
        }));

        var key = $"addressing/{Guid.CreateVersion7()}";
        var put = generator.GeneratePresignedPut(key, expirationSeconds: 600);

        // The shape first, and it is not decoration. MinIO answers both styles, so an upload
        // succeeding proves the URL is well formed but says nothing about which style was chosen —
        // and choosing the wrong one is precisely the defect this exists for. The signed host is the
        // only place the choice is visible, so it is asserted directly.
        var authority = new Uri(put.Url).Authority;

        Assert.Multiple(() =>
        {
            Assert.That(authority, Is.EqualTo(pathStyle
                    ? settings.S3Endpoint
                    : $"{settings.S3Bucket}.{settings.S3Endpoint}"),
                "the generator ignored the addressing option, which is how it came to disagree with "
              + "the server's own client in the first place");

            Assert.That(new Uri(put.Url).AbsolutePath.Contains($"/{settings.S3Bucket}/"), Is.EqualTo(pathStyle),
                "the bucket belongs in the path in one style and in the host in the other, never both "
              + "and never neither");
        });

        using var client  = DirectToStore();
        using var content = new ByteArrayContent(Png);

        content.Headers.TryAddWithoutValidation("Content-Type", "image/png");

        using var response = await client.PutAsync(put.Url, content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"the store refused a {(pathStyle ? "path-style" : "virtual-host")} presigned URL: "
          + await response.Content.ReadAsStringAsync());
    }

    // ── helpers ─────────────────────────────────────────────────────────────────────────────────

    private async Task<Guid> CurrentUserId(IServiceScope scope, CancellationToken ct)
        => (await GetUserService(scope.ServiceProvider).GetMe(ct)).userId;

    private static async Task<SuccessUploadFile> Begin(Func<Task<IUploadFileResult>> begin)
    {
        var result = await begin();

        if (result is FailedUploadFile failed)
            Assert.Fail($"the server would not sign an upload: {failed.error}");

        return (SuccessUploadFile)result;
    }

    /// <summary>
    /// Sends the bytes exactly as the signature demands.
    /// </summary>
    /// <remarks>
    /// The returned fields are applied verbatim and nothing is added: they are part of what was
    /// signed, so a client that improvises a header of its own gets a signature mismatch from the
    /// store rather than an error from us. That is also what makes this worth asserting on — the
    /// upload succeeding is the proof that what the server signed and what a client would send agree.
    /// </remarks>
    private static async Task Upload(SuccessUploadFile ticket, byte[] payload, string contentType = "image/png")
    {
        using var client  = DirectToStore();
        using var content = new ByteArrayContent(payload);

        // What a browser does: XMLHttpRequest sends the Blob's own type when no header was dictated.
        // The server signs no content type, so this one is the client's and travels unsigned.
        content.Headers.TryAddWithoutValidation("Content-Type", contentType);

        foreach (var field in ticket.formFields)
            content.Headers.TryAddWithoutValidation(field.key, field.value);

        using var response = await client.PutAsync(ticket.uploadUrl, content);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK),
            $"the object store refused the presigned upload: {await response.Content.ReadAsStringAsync()}");
    }

    /// <summary>
    /// An HTTP client that ignores DNS and dials the store's mapped port.
    /// </summary>
    /// <remarks>
    /// <c>argon-test.localhost</c> is a name no resolver on a test machine answers for, and the URL
    /// has to keep it because the host is signed into the request. Overriding the connection instead
    /// of the URL leaves the signature — and therefore what is being tested — untouched.
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
