namespace ArgonSharedLogicTest.Storage;

using Argon.Features.Storage;

/// <summary>
/// What the dedup step reads off an object's headers: an MD5 out of the ETag, and a media type that is
/// the same string for the same type however the client spelled it.
/// </summary>
[TestFixture]
public class BlobHashesTests
{
    [TestCase("\"d2c8ac3752a77dda6f833c61613f55da\"")]
    [TestCase("d2c8ac3752a77dda6f833c61613f55da")]
    [TestCase(" \"D2C8AC3752A77DDA6F833C61613F55DA\" ")]
    public void A_single_part_etag_is_the_md5(string etag)
        => Assert.That(BlobHashes.ParseEtagMd5(etag), Is.EqualTo(Convert.FromHexString("d2c8ac3752a77dda6f833c61613f55da")));

    /// <summary>A multipart ETag is a hash of hashes with a part count; anything else is not a hash at all.</summary>
    [TestCase("\"d2c8ac3752a77dda6f833c61613f55da-3\"")]
    [TestCase("\"not-a-hash\"")]
    [TestCase("\"zzc8ac3752a77dda6f833c61613f55da\"")]
    [TestCase("")]
    [TestCase(null)]
    public void Anything_but_a_single_part_etag_is_no_md5(string? etag)
        => Assert.That(BlobHashes.ParseEtagMd5(etag), Is.Null);

    [TestCase("image/png", "image/png")]
    [TestCase("Image/PNG", "image/png")]
    [TestCase(" image/jpeg ; charset=binary", "image/jpeg")]
    [TestCase("", "application/octet-stream")]
    [TestCase("   ", "application/octet-stream")]
    [TestCase(null, "application/octet-stream")]
    public void A_content_type_is_one_string_per_type(string? declared, string expected)
        => Assert.That(BlobHashes.NormalizeContentType(declared), Is.EqualTo(expected));
}
