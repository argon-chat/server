namespace ArgonSharedLogicTest.Expressions;

using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Argon.Features.Expressions;
using ArgonContracts;

[TestFixture]
public class LottieExpressionValidationTests
{
    private readonly ExpressionFileValidator validator = new();

    private ValueTask<ExpressionValidation> Validate(byte[] data, ExpressionKind kind = ExpressionKind.Sticker)
        => validator.ValidateAsync(new MemoryStream(data), kind, ExpressionFormat.Lottie, CancellationToken.None);

    private async Task<ExpressionError> ErrorOf(JsonObject document, ExpressionKind kind = ExpressionKind.Sticker)
        => (await Validate(TestLottie.Gzip(TestLottie.Json(document)), kind)).Error;

    [Test]
    public async Task A_gzipped_animation_is_accepted_as_uploaded()
    {
        var tgs = TestLottie.Gzip(TestLottie.Json(TestLottie.Document()));

        var result = await Validate(tgs);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.Format, Is.EqualTo(ExpressionFormat.Lottie));
            Assert.That((result.Width, result.Height), Is.EqualTo((512, 512)));
            Assert.That(result.ContentType, Is.EqualTo("application/x-tgsticker"));
            Assert.That(result.Reencoded, Is.Null);
            Assert.That(result.FileSize, Is.EqualTo(tgs.Length));
            Assert.That(result.DurationSeconds, Is.EqualTo(3.0));
            Assert.That(result.Fps, Is.EqualTo(60));
            Assert.That(result.Outline, Is.Null);
        });
    }

    [Test]
    public async Task A_telegram_export_with_the_tgs_marker_passes()
    {
        // Telegram's exporter writes "tgs": 1 at the root of every .tgs.
        var document = TestLottie.Document();
        document["tgs"] = 1;

        var result = await Validate(TestLottie.Gzip(TestLottie.Json(document)));

        Assert.That(result.Ok, Is.True, result.Error.ToString());
    }

    [Test]
    public async Task Plain_json_is_accepted_and_gzipped_for_storage()
    {
        var json = TestLottie.Json(TestLottie.Document());

        var result = await Validate(json);

        Assert.Multiple(() =>
        {
            Assert.That(result.Ok, Is.True);
            Assert.That(result.ContentType, Is.EqualTo("application/json"));
            Assert.That(result.ReencodedContentType, Is.EqualTo("application/x-tgsticker"));
            Assert.That(result.Reencoded, Is.Not.Null);
            Assert.That(result.Reencoded![..2], Is.EqualTo(new byte[] { 0x1F, 0x8B }));
            Assert.That(result.FileSize, Is.EqualTo(result.Reencoded.Length));
            Assert.That(TestLottie.Gunzip(result.Reencoded), Is.EqualTo(json));
        });
    }

    [Test]
    public async Task Plain_json_with_a_bom_is_accepted()
    {
        byte[] json = [0xEF, 0xBB, 0xBF, .. TestLottie.Json(TestLottie.Document())];

        Assert.That((await Validate(json)).Ok, Is.True);
    }

    [Test]
    public async Task An_emoji_is_100_square()
    {
        Assert.That(await ErrorOf(TestLottie.Document(side: 100), ExpressionKind.Emoji), Is.EqualTo(ExpressionError.NONE));
        Assert.That(await ErrorOf(TestLottie.Document(side: 512), ExpressionKind.Emoji), Is.EqualTo(ExpressionError.INVALID_FORMAT));
        Assert.That(await ErrorOf(TestLottie.Document(side: 100)), Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task Three_seconds_is_the_longest()
    {
        var document = TestLottie.Document();
        document["op"] = 181;

        Assert.That(await ErrorOf(document), Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [TestCase(61)]
    [TestCase(0)]
    [TestCase(-30)]
    public async Task The_frame_rate_is_above_zero_and_at_most_60(double fr)
    {
        var document = TestLottie.Document();
        document["fr"] = fr;

        Assert.That(await ErrorOf(document), Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task Thirty_fps_for_three_seconds_passes()
    {
        var document = TestLottie.Document();
        document["fr"] = 30;
        document["op"] = 90;

        var result = await Validate(TestLottie.Gzip(TestLottie.Json(document)));

        Assert.That(result.Ok, Is.True);
        Assert.That(result.DurationSeconds, Is.EqualTo(3.0));
    }

    [TestCase("w", 500)]
    [TestCase("h", 513)]
    public async Task The_canvas_is_exactly_512(string key, int value)
    {
        var document = TestLottie.Document();
        document[key] = value;

        Assert.That(await ErrorOf(document), Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [TestCase("w")]
    [TestCase("fr")]
    [TestCase("op")]
    [TestCase("layers")]
    public async Task A_required_member_cannot_be_missing(string key)
    {
        var document = TestLottie.Document();
        document.Remove(key);

        Assert.That(await ErrorOf(document), Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    private static IEnumerable<TestCaseData> ForbiddenLayers()
    {
        yield return Case("image layer", l => l["ty"] = 2);
        yield return Case("solid layer", l => l["ty"] = 1);
        yield return Case("text layer", l => l["ty"] = 5);
        yield return Case("3D layer", l => l["ddd"] = 1);
        yield return Case("auto-orient", l => l["ao"] = 1);
        yield return Case("time remap", l => l["tm"] = JsonNode.Parse("""{ "a": 0, "k": 0 }"""));
        yield return Case("mask", l => l["masksProperties"] = JsonNode.Parse("""[{ "mode": "a" }]"""));
        yield return Case("hasMask", l => l["hasMask"] = true);
        yield return Case("layer effect", l => l["ef"] = JsonNode.Parse("""[{ "ty": 5, "ef": [] }]"""));
        yield return Case("time stretch", l => l["sr"] = 2);

        static TestCaseData Case(string name, Action<JsonObject> change)
            => new TestCaseData(change).SetName($"A layer with a {name} is refused");
    }

    [TestCaseSource(nameof(ForbiddenLayers))]
    public async Task Forbidden_layer(Action<JsonObject> change)
    {
        var document = TestLottie.Document();
        change(TestLottie.Layer(document));

        Assert.That(await ErrorOf(document), Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [TestCase("gs")]
    [TestCase("rp")]
    [TestCase("mm")]
    [TestCase("sr")]
    public async Task A_forbidden_shape_is_refused_even_inside_a_group(string type)
    {
        var document = TestLottie.Document();
        TestLottie.GroupItems(document).Add(new JsonObject { ["ty"] = type });

        Assert.That(await ErrorOf(document), Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task An_expression_on_a_property_is_refused()
    {
        var document = TestLottie.Document();
        TestLottie.Layer(document)["ks"]!["o"] = JsonNode.Parse("""{ "a": 0, "k": 100, "x": "var $bm_rt = time * 100;" }""");

        Assert.That(await ErrorOf(document), Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task A_3d_composition_is_refused()
    {
        var document = TestLottie.Document();
        document["ddd"] = 1;

        Assert.That(await ErrorOf(document), Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [TestCase("""{ "id": "img_0", "w": 10, "h": 10, "u": "images/", "p": "img_0.png", "e": 0 }""")]
    [TestCase("""{ "id": "img_0", "w": 10, "h": 10, "u": "", "p": "data:image/png;base64,iVBORw0KGgo=", "e": 1 }""")]
    public async Task An_image_asset_is_refused(string asset)
    {
        var document = TestLottie.Document();
        document["assets"]!.AsArray().Add(JsonNode.Parse(asset));

        Assert.That(await ErrorOf(document), Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task A_precomp_is_allowed_and_its_layers_are_checked_too()
    {
        var document = TestLottie.Document();
        var inner    = TestLottie.Layer(document).DeepClone();
        document["assets"]!.AsArray().Add(new JsonObject { ["id"] = "comp_0", ["layers"] = new JsonArray(inner) });
        document["layers"]!.AsArray().Add(JsonNode.Parse(
            """{ "ddd": 0, "ind": 2, "ty": 0, "refId": "comp_0", "sr": 1, "ks": {}, "w": 512, "h": 512, "ip": 0, "op": 180, "st": 0 }"""));

        Assert.That(await ErrorOf(document), Is.EqualTo(ExpressionError.NONE));

        document["assets"]![0]!["layers"]![0]!["ty"] = 2;

        Assert.That(await ErrorOf(document), Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task Nesting_within_the_depth_cap_is_walked_and_nesting_past_it_is_refused()
    {
        var shallow = TestLottie.Document();
        TestLottie.Layer(shallow)["junk"] = JsonNode.Parse(new string('[', 58) + new string(']', 58));

        var deep = Encoding.UTF8.GetBytes(TestLottie.Document().ToJsonString()
           .Replace("\"nm\":\"test\"", "\"nm\":" + new string('[', 100_000) + new string(']', 100_000)));

        Assert.That(await ErrorOf(shallow), Is.EqualTo(ExpressionError.NONE));
        Assert.That((await Validate(deep)).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    [Test]
    public async Task A_gzip_bomb_is_stopped_at_the_decompression_cap()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write("{\"nm\":\""u8);
            var spaces = new byte[1024 * 1024];
            Array.Fill(spaces, (byte)' ');
            for (var i = 0; i < 16; i++)
                gzip.Write(spaces);
        }

        var bomb = output.ToArray();
        Assert.That(bomb.Length, Is.LessThan(ExpressionLimits.StickerLottieMaxBytes), "the bomb must fit the upload cap");

        var result = await Validate(bomb);

        Assert.That(result.Error, Is.EqualTo(ExpressionError.TOO_LARGE));
    }

    [Test]
    public async Task A_tgs_over_64_kb_is_too_large()
    {
        var document = TestLottie.Document();
        document["nm"] = Convert.ToBase64String(RandomBytes(100 * 1024));

        var json = TestLottie.Json(document);

        Assert.That((await Validate(TestLottie.Gzip(json))).Error, Is.EqualTo(ExpressionError.TOO_LARGE));
        Assert.That((await Validate(json)).Error, Is.EqualTo(ExpressionError.TOO_LARGE));
    }

    [Test]
    public async Task Plain_json_over_the_decompressed_cap_is_too_large()
    {
        var json = new byte[ExpressionLimits.LottieMaxJsonBytes + 1];
        Array.Fill(json, (byte)' ');
        json[0] = (byte)'{';

        Assert.That((await Validate(json)).Error, Is.EqualTo(ExpressionError.TOO_LARGE));
    }

    [Test]
    public async Task Bytes_that_are_neither_gzip_nor_json_are_refused()
    {
        Assert.That((await Validate("not a lottie"u8.ToArray())).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
        Assert.That((await Validate([0x1F, 0x8B, 0x08, 0x00, 0x13, 0x37])).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
        Assert.That((await Validate("[1, 2, 3]"u8.ToArray())).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
        Assert.That((await Validate("{ \"w\": 512,"u8.ToArray())).Error, Is.EqualTo(ExpressionError.INVALID_FORMAT));
    }

    private static byte[] RandomBytes(int length)
    {
        var bytes = new byte[length];
        new Random(11).NextBytes(bytes);
        return bytes;
    }
}
