namespace ArgonComplexTest.Tests;

using Argon.Entities;
using Argon.Features.Integrations.Klipy;
using Argon.Features.Storage;
using ArgonComplexTest.Infrastructure.Account;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// A user's saved GIFs: saving one out of Klipy (cached into our own store on first save), the
/// order the list keeps, removing, and the soft limit that evicts the oldest.
/// </summary>
/// <remarks>
/// Klipy itself is <see cref="FakeKlipyApi"/>, answered in process, so a slug a test publishes is
/// fetched, downloaded and put into the object store by the shipped <c>KlipyService</c>.
/// </remarks>
[TestFixture]
public class SavedGifTests : TestBase
{
    private static readonly byte[] Webp = [0x52, 0x49, 0x46, 0x46, 0x1a, 0, 0, 0, 0x57, 0x45, 0x42, 0x50];

    private FakeKlipyApi Klipy => FactoryAsp.Services.GetRequiredService<FakeKlipyApi>();

    private IKlipyService KlipyService => FactoryAsp.Services.GetRequiredService<IKlipyService>();

    private IGifInteraction Gifs(TestUserSession session) => session.Client.ForService<IGifInteraction>(FactoryAsp.Services);

    private string PublishSlug()
    {
        var slug = $"sg-{Guid.NewGuid():N}";
        Klipy.Publish(slug, Webp);
        return slug;
    }

    /// <summary>What the client does: find the GIF, then save it with the token the search handed out.</summary>
    private async Task<SavedGif> SaveFromSearchAsync(TestUserSession session, string slug, CancellationToken ct)
    {
        var found = await Gifs(session).Search(slug, 0, 10, ct);
        var item  = found.items.Values.Single(x => x.gifId == slug);

        var result = await Gifs(session).SaveGif(item.gifId, item.hmac, ct);

        Assert.That(result, Is.InstanceOf<SuccessSaveGif>(), $"saving '{slug}' was refused: {(result as FailedSaveGif)?.error}");
        return ((SuccessSaveGif)result).gif;
    }

    private static async Task<long?> RefCountAsync(Guid fileId, CancellationToken ct)
    {
        await using var db = await SocialHarness.DbAsync(ct);
        return await db.FileCounters.AsNoTracking().Where(c => c.Id == fileId).Select(c => (long?)c.RefCount).FirstOrDefaultAsync(ct);
    }

    /// <summary>One cached file with a counter of <paramref name="refs"/>, standing in for a GIF already in our store.</summary>
    private static async Task<Guid> SeedCachedFileAsync(string s3Key, long refs, CancellationToken ct)
    {
        await using var db = await SocialHarness.DbAsync(ct);

        var fileId = Guid.CreateVersion7();
        var now    = DateTimeOffset.UtcNow;

        db.Files.Add(new FileEntity
        {
            Id = fileId, OwnerId = Guid.Empty, Purpose = FilePurpose.Gif, S3Key = s3Key, BucketName = "cdn",
            FileSize = Webp.Length, ContentType = "image/webp", Finalized = true, CreatedAt = now, UpdatedAt = now
        });
        db.FileCounters.Add(new FileCounterEntity { Id = fileId, RefCount = refs, CreatedAt = now, UpdatedAt = now });

        await db.SaveChangesAsync(ct);
        return fileId;
    }

    /// <summary>
    /// <paramref name="count"/> saved GIFs, oldest first a minute apart, all holding one reference each on
    /// <paramref name="fileId"/>.
    /// </summary>
    private static async Task<List<Guid>> SeedSavedAsync(Guid userId, Guid fileId, int count, CancellationToken ct)
    {
        await using var db = await SocialHarness.DbAsync(ct);

        var start = DateTimeOffset.UtcNow.AddDays(-1);
        var ids   = new List<Guid>(count);

        for (var i = 0; i < count; i++)
        {
            var id = Guid.CreateVersion7();
            ids.Add(id);
            db.SavedGifs.Add(new SavedGifEntity
            {
                Id = id, UserId = userId, FileId = fileId, Slug = $"seed-{id:N}", AddedAt = start.AddMinutes(i),
                CreatedAt = start, UpdatedAt = start
            });
        }

        await db.SaveChangesAsync(ct);
        return ids;
    }

    private static async Task<int> SavedCountAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await SocialHarness.DbAsync(ct);
        return await db.SavedGifs.CountAsync(x => x.UserId == userId, ct);
    }

    [Test, CancelAfter(120_000)]
    public async Task Saving_an_uncached_gif_caches_it_once_and_a_second_saver_shares_the_file(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);
        var slug  = PublishSlug();

        var first = await SaveFromSearchAsync(alice, slug, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(first.gifId, Is.EqualTo(slug));
            Assert.That(first.previewUrl, Does.EndWith(KlipyService.ComputeCachePath(slug)),
                "the saved GIF has to be served from our cache, not from Klipy");
            Assert.That(first.webmUrl, Is.EqualTo(first.previewUrl));
            Assert.That(Klipy.LookupsOf(slug), Is.EqualTo(1));
            Assert.That(await RefCountAsync(first.fileId, ct), Is.EqualTo(1), "the cache entry is born holding the first saver's reference");
        });

        var second = await SaveFromSearchAsync(bob, slug, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(second.fileId, Is.EqualTo(first.fileId), "a GIF already in the cache must not be cached a second time");
            Assert.That(Klipy.LookupsOf(slug), Is.EqualTo(1), "Klipy was asked again for a GIF we already hold");
            Assert.That(await RefCountAsync(first.fileId, ct), Is.EqualTo(2), "the second saver holds a reference of their own");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Saving_a_gif_klipy_does_not_know_is_not_found(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var slug  = $"sg-missing-{Guid.NewGuid():N}";

        var result = await Gifs(alice).SaveGif(slug, KlipyService.ComputeUserHmac(slug, alice.UserId), ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(result, Is.InstanceOf<FailedSaveGif>());
            Assert.That((result as FailedSaveGif)?.error, Is.EqualTo(SaveGifError.NOT_FOUND));
            Assert.That(await SavedCountAsync(alice.UserId, ct), Is.Zero);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task A_token_minted_for_someone_else_or_forged_is_refused(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);
        var slug  = PublishSlug();

        var forBob = await Gifs(alice).SaveGif(slug, KlipyService.ComputeUserHmac(slug, bob.UserId), ct);
        var forged = await Gifs(alice).SaveGif(slug, "00", ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((forBob as FailedSaveGif)?.error, Is.EqualTo(SaveGifError.INVALID_HMAC));
            Assert.That((forged as FailedSaveGif)?.error, Is.EqualTo(SaveGifError.INVALID_HMAC));
            Assert.That(Klipy.LookupsOf(slug), Is.Zero, "a refused token must not reach Klipy");
            Assert.That(await SavedCountAsync(alice.UserId, ct), Is.Zero);
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task Saving_again_moves_the_gif_to_the_top_without_a_second_row_or_reference(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var a     = await SaveFromSearchAsync(alice, PublishSlug(), ct);
        var b     = await SaveFromSearchAsync(alice, PublishSlug(), ct);

        var before = await Gifs(alice).GetSavedGifs(0, 50, ct);
        Assert.That(before.Values.Select(x => x.id), Is.EqualTo(new[] { b.id, a.id }), "newest first");

        var again = await SaveFromSearchAsync(alice, a.gifId!, ct);
        var after = await Gifs(alice).GetSavedGifs(0, 50, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(again.id, Is.EqualTo(a.id), "re-saving is a bump, not a new entry");
            Assert.That(after.Values.Select(x => x.id), Is.EqualTo(new[] { a.id, b.id }));
            Assert.That(await RefCountAsync(a.fileId, ct), Is.EqualTo(1), "a bump must not take another reference");
        });

        var page = await Gifs(alice).GetSavedGifs(1, 1, ct);
        Assert.That(page.Values.Select(x => x.id), Is.EqualTo(new[] { b.id }), "page 1 of size 1 is the second entry");
    }

    [Test, CancelAfter(120_000)]
    public async Task Removing_releases_the_reference_and_only_the_owner_can_remove(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var bob   = await CreateSessionAsync(ct);
        var saved = await SaveFromSearchAsync(alice, PublishSlug(), ct);

        Assert.That(await Gifs(bob).RemoveSavedGif(saved.id, ct), Is.False, "bob removed alice's GIF");
        Assert.That(await SavedCountAsync(alice.UserId, ct), Is.EqualTo(1));

        Assert.That(await Gifs(alice).RemoveSavedGif(saved.id, ct), Is.True);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((await Gifs(alice).GetSavedGifs(0, 50, ct)).Size, Is.Zero);
            Assert.That(await RefCountAsync(saved.fileId, ct), Is.Zero);
            Assert.That(await Gifs(alice).RemoveSavedGif(saved.id, ct), Is.False, "a second removal has nothing to remove");
            Assert.That(await RefCountAsync(saved.fileId, ct), Is.Zero, "a second removal must not release twice");
        });
    }

    /// <summary>
    /// Removing a GIF and saving it again is the ordinary way to move it back into the list.
    /// </summary>
    /// <remarks>
    /// <c>RemoveSavedGifAsync</c> soft-deletes (the context's interceptor turns the removal into
    /// <c>IsDeleted</c>), and the unique index on <c>(UserId, Slug)</c> does not exclude deleted rows,
    /// so the insert on the second save collided with the tombstone of the first.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_removed_gif_can_be_saved_again(CancellationToken ct = default)
    {
        var alice = await CreateSessionAsync(ct);
        var slug  = PublishSlug();

        var first = await SaveFromSearchAsync(alice, slug, ct);
        Assert.That(await Gifs(alice).RemoveSavedGif(first.id, ct), Is.True);

        var again = await SaveFromSearchAsync(alice, slug, ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(again.id, Is.Not.EqualTo(first.id));
            Assert.That((await Gifs(alice).GetSavedGifs(0, 50, ct)).Values.Select(x => x.gifId), Is.EqualTo(new[] { slug }));
            Assert.That(await RefCountAsync(again.fileId, ct), Is.EqualTo(1));
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task At_the_free_limit_the_oldest_is_evicted_and_its_reference_released(CancellationToken ct = default)
    {
        var alice  = await CreateSessionAsync(ct);
        var limit  = FactoryAsp.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<KlipyOptions>>().Value.SavedGifLimitFree;
        var shared = await SeedCachedFileAsync($"gifs/seed-{Guid.NewGuid():N}", limit, ct);
        var seeded = await SeedSavedAsync(alice.UserId, shared, limit, ct);

        var saved = await SaveFromSearchAsync(alice, PublishSlug(), ct);

        await using var db = await SocialHarness.DbAsync(ct);
        var remaining = await db.SavedGifs.Where(x => x.UserId == alice.UserId).Select(x => x.Id).ToListAsync(ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(remaining, Has.Count.EqualTo(limit), "the list must stay at the visible limit");
            Assert.That(remaining, Does.Not.Contain(seeded[0]), "the oldest has to be the one evicted");
            Assert.That(remaining, Does.Contain(seeded[1]));
            Assert.That(remaining, Does.Contain(saved.id));
            Assert.That(await RefCountAsync(shared, ct), Is.EqualTo(limit - 1), "the evicted GIF's reference was not released");
        });
    }

    [Test, CancelAfter(120_000)]
    public async Task An_ultima_account_keeps_saving_past_the_free_limit(CancellationToken ct = default)
    {
        var alice  = await CreateSessionAsync(ct);
        var limit  = FactoryAsp.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<KlipyOptions>>().Value.SavedGifLimitFree;
        var shared = await SeedCachedFileAsync($"gifs/seed-{Guid.NewGuid():N}", limit, ct);

        await SeedSavedAsync(alice.UserId, shared, limit, ct);
        await AccountSeed.SetUltimaAsync(alice.UserId, true, ct);

        await SaveFromSearchAsync(alice, PublishSlug(), ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That(await SavedCountAsync(alice.UserId, ct), Is.EqualTo(limit + 1));
            Assert.That(await RefCountAsync(shared, ct), Is.EqualTo(limit), "nothing was evicted, so nothing is released");
        });
    }

    /// <summary>
    /// A save at the limit that then fails must leave the list exactly as it was.
    /// </summary>
    /// <remarks>
    /// The eviction used to release the oldest GIF's file reference before the save was known to
    /// succeed. A GIF Klipy cannot find returns <c>NOT_FOUND</c> without saving, so the evicted row
    /// stayed in the list while its file had lost the reference — and a file at zero is the garbage
    /// collector's to delete.
    /// </remarks>
    [Test, CancelAfter(120_000)]
    public async Task A_failed_save_at_the_limit_releases_nothing(CancellationToken ct = default)
    {
        var alice  = await CreateSessionAsync(ct);
        var limit  = FactoryAsp.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<KlipyOptions>>().Value.SavedGifLimitFree;
        var shared = await SeedCachedFileAsync($"gifs/seed-{Guid.NewGuid():N}", limit, ct);

        await SeedSavedAsync(alice.UserId, shared, limit, ct);

        var slug   = $"sg-missing-{Guid.NewGuid():N}";
        var result = await Gifs(alice).SaveGif(slug, KlipyService.ComputeUserHmac(slug, alice.UserId), ct);

        await Assert.MultipleAsync(async () =>
        {
            Assert.That((result as FailedSaveGif)?.error, Is.EqualTo(SaveGifError.NOT_FOUND));
            Assert.That(await SavedCountAsync(alice.UserId, ct), Is.EqualTo(limit));
            Assert.That(await RefCountAsync(shared, ct), Is.EqualTo(limit),
                "a save that did not happen released the reference of a GIF that is still in the list");
        });
    }

    /// <summary>A saved GIF with no Klipy slug is one the user uploaded, and it is served from their own prefix.</summary>
    [Test, CancelAfter(120_000)]
    public async Task A_saved_gif_without_a_slug_is_served_from_the_owners_prefix(CancellationToken ct = default)
    {
        var alice  = await CreateSessionAsync(ct);
        var fileId = Guid.CreateVersion7();

        await using (var db = await SocialHarness.DbAsync(ct))
        {
            var now = DateTimeOffset.UtcNow;
            db.SavedGifs.Add(new SavedGifEntity
            {
                Id = Guid.CreateVersion7(), UserId = alice.UserId, FileId = fileId, Slug = null, Width = 16, Height = 9,
                AddedAt = now, CreatedAt = now, UpdatedAt = now
            });
            await db.SaveChangesAsync(ct);
        }

        var saved = (await Gifs(alice).GetSavedGifs(0, 10, ct)).Values.Single();

        Assert.Multiple(() =>
        {
            Assert.That(saved.gifId, Is.Null);
            Assert.That(saved.previewUrl, Does.EndWith($"u/{alice.UserId}/gif/{fileId}"));
            Assert.That((saved.width, saved.height), Is.EqualTo((16, 9)));
        });
    }
}
