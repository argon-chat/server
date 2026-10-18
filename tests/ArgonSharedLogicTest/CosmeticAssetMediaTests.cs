namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;

/// <summary>
/// Which media types each sort of cosmetic file accepts, and how a refusal names the sort.
/// </summary>
/// <remarks>
/// The check is against what the store actually received, so it is the last word on whether a
/// clip slot holds a clip: a still uploaded into a background's video slot is refused here rather
/// than published as a background that never plays.
/// </remarks>
[TestFixture]
public class CosmeticAssetMediaTests
{
    [TestCase(CosmeticAssetKind.Image, "image/png")]
    [TestCase(CosmeticAssetKind.Image, "IMAGE/WEBP")]
    [TestCase(CosmeticAssetKind.SpriteSheet, "image/png")]
    [TestCase(CosmeticAssetKind.SpriteSheet, "Image/Avif")]
    [TestCase(CosmeticAssetKind.Video, "video/webm")]
    [TestCase(CosmeticAssetKind.Video, "VIDEO/MP4")]
    [TestCase(CosmeticAssetKind.Font, "font/woff2")]
    [TestCase(CosmeticAssetKind.Font, "application/font-woff2")]
    [TestCase(CosmeticAssetKind.Font, "application/x-font-ttf")]
    [TestCase(CosmeticAssetKind.Font, "application/vnd.ms-opentype")]
    [TestCase(CosmeticAssetKind.Font, "application/octet-stream")]
    public void A_file_of_the_right_media_type_is_accepted(CosmeticAssetKind kind, string contentType)
        => Assert.That(CosmeticAssetMedia.Accepts(kind, contentType), Is.True);

    [TestCase(CosmeticAssetKind.Image, "video/webm")]
    [TestCase(CosmeticAssetKind.SpriteSheet, "video/webm")]
    [TestCase(CosmeticAssetKind.Video, "image/png")]
    [TestCase(CosmeticAssetKind.Video, "image/gif")]
    [TestCase(CosmeticAssetKind.Font, "image/png")]
    public void A_file_of_another_media_type_is_refused(CosmeticAssetKind kind, string contentType)
        => Assert.That(CosmeticAssetMedia.Accepts(kind, contentType), Is.False);

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void A_file_the_store_could_not_type_is_refused_for_every_sort(string? contentType)
    {
        Assert.Multiple(() =>
        {
            foreach (var kind in Enum.GetValues<CosmeticAssetKind>())
            {
                Assert.That(CosmeticAssetMedia.Accepts(kind, contentType), Is.False, kind.ToString());
            }
        });
    }

    /// <summary>A sort this build does not know accepts nothing rather than everything.</summary>
    [Test]
    public void A_sort_this_build_does_not_know_accepts_nothing()
        => Assert.That(CosmeticAssetMedia.Accepts((CosmeticAssetKind)99, "image/png"), Is.False);

    [TestCase(CosmeticAssetKind.Image, "an image")]
    [TestCase(CosmeticAssetKind.SpriteSheet, "a sprite sheet image")]
    [TestCase(CosmeticAssetKind.Video, "a video")]
    [TestCase(CosmeticAssetKind.Font, "a font file")]
    [TestCase((CosmeticAssetKind)99, "99")]
    public void A_sort_is_named_in_words(CosmeticAssetKind kind, string words)
        => Assert.That(CosmeticAssetMedia.Describe(kind), Is.EqualTo(words));
}
