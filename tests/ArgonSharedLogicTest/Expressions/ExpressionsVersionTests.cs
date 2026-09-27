namespace ArgonSharedLogicTest.Expressions;

using Argon.Features.Expressions;
using ArgonContracts;
using ion.runtime;

[TestFixture]
public class ExpressionsVersionTests
{
    private static readonly Guid A = Guid.NewGuid();
    private static readonly Guid B = Guid.NewGuid();

    [Test]
    public void A_token_is_sixteen_hex_characters_and_depends_only_on_its_input()
    {
        var token = ExpressionsVersion.Of([(A, 1L), (B, 2L)]);

        Assert.Multiple(() =>
        {
            Assert.That(token, Does.Match("^[0-9A-F]{16}$"));
            Assert.That(ExpressionsVersion.Of([(A, 1L), (B, 2L)]), Is.EqualTo(token));
        });
    }

    [Test]
    public void A_version_bump_an_added_pack_and_a_new_order_each_change_the_token()
    {
        var token = ExpressionsVersion.Of([(A, 1L), (B, 2L)]);

        Assert.Multiple(() =>
        {
            Assert.That(ExpressionsVersion.Of([(A, 2L), (B, 2L)]), Is.Not.EqualTo(token));
            Assert.That(ExpressionsVersion.Of([(A, 1L)]), Is.Not.EqualTo(token));
            Assert.That(ExpressionsVersion.Of([(B, 2L), (A, 1L)]), Is.Not.EqualTo(token));
        });
    }

    [Test]
    public void No_packs_is_one_fixed_token()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpressionsVersion.Of(Array.Empty<(Guid, long)>()), Is.EqualTo(ExpressionsVersion.Empty));
            Assert.That(ExpressionsVersion.Of(Array.Empty<ExpressionPack>()), Is.EqualTo(ExpressionsVersion.Empty));
            Assert.That(ExpressionsVersion.Empty, Does.Match("^[0-9A-F]{16}$"));
        });
    }

    [Test]
    public void The_token_of_packs_is_that_of_their_ids_and_versions()
    {
        var packs = new[]
        {
            new ExpressionPack(A, Guid.NewGuid(), ExpressionKind.Emoji, "a", "a", null, 0, 3, IonArray<ExpressionItem>.Empty, null),
            new ExpressionPack(B, Guid.NewGuid(), ExpressionKind.Sticker, "b", "b", null, 0, 5, IonArray<ExpressionItem>.Empty, null)
        };

        Assert.That(ExpressionsVersion.Of(packs), Is.EqualTo(ExpressionsVersion.Of([(A, 3L), (B, 5L)])));
    }
}

[TestFixture]
public class ExpressionUploadsTests
{
    [Test]
    public void Formats_are_told_apart_by_their_bytes()
    {
        var png  = TestImages.Png(TestImages.Disc(8, 8));
        var webp = TestImages.Webp(TestImages.Disc(8, 8));
        var json = TestLottie.Json(TestLottie.Document());
        var tgs  = TestLottie.Gzip(json);
        var webm = new byte[] { 0x1A, 0x45, 0xDF, 0xA3, 0x01 };

        Assert.Multiple(() =>
        {
            Assert.That(ExpressionUploads.Sniff(png), Is.EqualTo(ExpressionFormat.Static));
            Assert.That(ExpressionUploads.Sniff(webp), Is.EqualTo(ExpressionFormat.Static));
            Assert.That(ExpressionUploads.Sniff(json), Is.EqualTo(ExpressionFormat.Lottie));
            Assert.That(ExpressionUploads.Sniff([0xEF, 0xBB, 0xBF, (byte)' ', (byte)'{']), Is.EqualTo(ExpressionFormat.Lottie));
            Assert.That(ExpressionUploads.Sniff(tgs), Is.EqualTo(ExpressionFormat.Lottie));
            Assert.That(ExpressionUploads.Sniff(webm), Is.EqualTo(ExpressionFormat.Video));
            Assert.That(ExpressionUploads.Sniff(TestImages.Jpeg(TestImages.Disc(8, 8))), Is.Null);
            Assert.That(ExpressionUploads.Sniff([]), Is.Null);
        });
    }

    [TestCase(ExpressionFormat.Static, "image/png", true)]
    [TestCase(ExpressionFormat.Static, "IMAGE/WEBP; q=1", true)]
    [TestCase(ExpressionFormat.Static, "image/gif", false)]
    [TestCase(ExpressionFormat.Lottie, "application/x-tgsticker", true)]
    [TestCase(ExpressionFormat.Lottie, "application/gzip", true)]
    [TestCase(ExpressionFormat.Lottie, "application/json", true)]
    [TestCase(ExpressionFormat.Lottie, "application/octet-stream", true)]
    [TestCase(ExpressionFormat.Lottie, "image/png", false)]
    [TestCase(ExpressionFormat.Video, "video/webm", true)]
    [TestCase(ExpressionFormat.Video, "video/mp4", false)]
    public void A_format_accepts_its_own_content_types(ExpressionFormat format, string contentType, bool accepted)
        => Assert.That(ExpressionUploads.Accepts(format, contentType), Is.EqualTo(accepted));

    [Test]
    public void A_plain_json_lottie_may_arrive_larger_than_its_stored_cap()
    {
        Assert.Multiple(() =>
        {
            Assert.That(ExpressionUploads.MaxUploadBytes(ExpressionKind.Sticker, ExpressionFormat.Lottie), Is.EqualTo(ExpressionLimits.LottieMaxJsonBytes));
            Assert.That(ExpressionUploads.MaxUploadBytes(ExpressionKind.Sticker, ExpressionFormat.Static), Is.EqualTo(ExpressionLimits.StickerStaticMaxBytes));
            Assert.That(ExpressionUploads.MaxUploadBytes(ExpressionKind.Emoji), Is.EqualTo(ExpressionLimits.LottieMaxJsonBytes));
        });
    }

    [Test]
    public void A_client_outline_is_kept_only_when_small_enough()
    {
        var outline = OutlineCodec.Encode("M10,10l20,0l0,20z");

        Assert.Multiple(() =>
        {
            Assert.That(ExpressionUploads.AcceptOutline(outline), Is.EqualTo(outline));
            Assert.That(ExpressionUploads.AcceptOutline(null), Is.Null);
            Assert.That(ExpressionUploads.AcceptOutline([]), Is.Null);
            Assert.That(ExpressionUploads.AcceptOutline(new byte[ExpressionLimits.OutlineMaxBytes + 1]), Is.Null);
        });
    }
}
