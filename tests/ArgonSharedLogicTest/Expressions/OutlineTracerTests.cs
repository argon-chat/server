namespace ArgonSharedLogicTest.Expressions;

using Argon.Features.Expressions;

[TestFixture]
public class OutlineTracerTests
{
    private static List<List<(int X, int Y)>> Trace(byte[] alpha, int width, int height, int maxBytes = 1024)
    {
        var bytes = OutlineTracer.FromAlpha(alpha, width, height, maxBytes);
        Assert.That(bytes, Is.Not.Null);
        Assert.That(bytes!.Length, Is.LessThanOrEqualTo(maxBytes));
        return OutlinePath.Parse(OutlineCodec.Decode(bytes));
    }

    private static byte[] Filled(int width, int height, Func<int, int, bool> inside)
    {
        var alpha = new byte[width * height];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
            alpha[y * width + x] = inside(x, y) ? (byte)255 : (byte)0;
        return alpha;
    }

    [Test]
    public void A_disc_becomes_one_closed_contour_spanning_its_extent_in_the_512_box()
    {
        using var image = TestImages.Disc(512, 512, 256, 256, 200);

        var contours = Trace(TestImages.Alpha(image), 512, 512);

        Assert.That(contours, Has.Count.EqualTo(1));
        var ring = contours[0];

        Assert.Multiple(() =>
        {
            Assert.That(ring, Has.Count.InRange(8, 400));
            Assert.That(ring.Min(p => p.X), Is.InRange(50, 62));
            Assert.That(ring.Max(p => p.X), Is.InRange(450, 462));
            Assert.That(ring.Min(p => p.Y), Is.InRange(50, 62));
            Assert.That(ring.Max(p => p.Y), Is.InRange(450, 462));
            Assert.That(Math.Abs(OutlinePath.SignedArea(ring)), Is.EqualTo(Math.PI * 200 * 200).Within(3).Percent);
        });
    }

    [Test]
    public void A_fully_opaque_square_is_the_whole_box()
    {
        var contours = Trace(Filled(512, 512, (_, _) => true), 512, 512);

        Assert.That(contours, Has.Count.EqualTo(1));
        Assert.That(contours[0], Is.EqualTo(new[] { (0, 0), (512, 0), (512, 512), (0, 512) }));
    }

    [Test]
    public void A_fully_opaque_wide_image_is_centred_with_its_aspect()
    {
        var contours = Trace(Filled(512, 256, (_, _) => true), 512, 256);

        Assert.That(contours[0], Is.EqualTo(new[] { (0, 128), (512, 128), (512, 384), (0, 384) }));
    }

    [Test]
    public void An_opaque_rectangle_inside_transparency_keeps_its_corners()
    {
        var contours = Trace(Filled(512, 512, (x, y) => x is >= 100 and < 400 && y is >= 150 and < 350), 512, 512);

        Assert.That(contours, Has.Count.EqualTo(1));
        var ring = contours[0];

        Assert.Multiple(() =>
        {
            Assert.That(ring, Has.Count.GreaterThanOrEqualTo(4));
            Assert.That(ring.Min(p => p.X), Is.InRange(98, 104));
            Assert.That(ring.Max(p => p.X), Is.InRange(396, 402));
            Assert.That(ring.Min(p => p.Y), Is.InRange(148, 154));
            Assert.That(ring.Max(p => p.Y), Is.InRange(346, 352));
        });
    }

    [Test]
    public void A_ring_keeps_its_hole_with_the_opposite_winding()
    {
        using var image = TestImages.Disc(512, 512, 256, 256, 220, hole: 100);

        var contours = Trace(TestImages.Alpha(image), 512, 512);

        Assert.That(contours, Has.Count.EqualTo(2));
        var outer = OutlinePath.SignedArea(contours[0]);
        var inner = OutlinePath.SignedArea(contours[1]);

        Assert.Multiple(() =>
        {
            Assert.That(Math.Abs(outer), Is.GreaterThan(Math.Abs(inner)));
            Assert.That(Math.Sign(outer), Is.EqualTo(-Math.Sign(inner)));
        });
    }

    [Test]
    public void Separate_blobs_each_get_a_contour()
    {
        var alpha = Filled(512, 512, (x, y) =>
            (x is >= 20 and < 200 && y is >= 20 and < 200) || (x is >= 300 and < 480 && y is >= 300 and < 480));

        Assert.That(Trace(alpha, 512, 512), Has.Count.EqualTo(2));
    }

    [Test]
    public void An_empty_mask_has_no_outline()
        => Assert.That(OutlineTracer.FromAlpha(new byte[512 * 512], 512, 512), Is.Null);

    [Test]
    public void Semi_transparent_pixels_below_the_threshold_do_not_count()
        => Assert.That(OutlineTracer.FromAlpha(Enumerable.Repeat((byte)127, 64 * 64).ToArray(), 64, 64), Is.Null);

    [Test]
    public void A_noisy_mask_stays_within_the_byte_budget()
    {
        var random = new Random(42);
        var alpha  = new byte[512 * 512];
        for (var by = 0; by < 512; by += 8)
        for (var bx = 0; bx < 512; bx += 8)
        {
            var on = random.Next(2) == 0;
            for (var y = by; y < by + 8; y++)
            for (var x = bx; x < bx + 8; x++)
                alpha[y * 512 + x] = on ? (byte)255 : (byte)0;
        }

        var bytes = OutlineTracer.FromAlpha(alpha, 512, 512);

        if (bytes is not null)
            Assert.That(bytes.Length, Is.LessThanOrEqualTo(1024));
    }

    [Test]
    public void A_tight_budget_simplifies_harder_or_gives_up()
    {
        using var image = TestImages.Disc(512, 512, 256, 256, 200);
        var alpha = TestImages.Alpha(image);

        var roomy = OutlineTracer.FromAlpha(alpha, 512, 512)!;
        var tight = OutlineTracer.FromAlpha(alpha, 512, 512, maxBytes: 40);

        Assert.That(tight, Is.Not.Null);
        Assert.That(tight!.Length, Is.LessThanOrEqualTo(40).And.LessThan(roomy.Length));
        Assert.That(OutlineTracer.FromAlpha(alpha, 512, 512, maxBytes: 4), Is.Null);
    }

    [Test]
    public void Small_images_are_traced_without_downsampling()
    {
        var contours = Trace(Filled(100, 100, (x, y) => x is >= 25 and < 75 && y is >= 25 and < 75), 100, 100);

        Assert.That(contours, Has.Count.EqualTo(1));
        Assert.That(contours[0].Min(p => p.X), Is.InRange(125, 132));
        Assert.That(contours[0].Max(p => p.X), Is.InRange(380, 387));
    }

    [Test]
    public void The_image_overload_reads_the_alpha_channel()
    {
        using var image = TestImages.Disc(300, 512);

        Assert.That(OutlineTracer.FromAlpha(image), Is.EqualTo(OutlineTracer.FromAlpha(TestImages.Alpha(image), 300, 512)));
    }
}
