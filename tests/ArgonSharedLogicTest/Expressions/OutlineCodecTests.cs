namespace ArgonSharedLogicTest.Expressions;

using System.Text;
using Argon.Features.Expressions;

[TestFixture]
public class OutlineCodecTests
{
    [TestCase("M10,10l5,-3l-5,0z")]
    [TestCase("M256,0l-256,256z")]
    [TestCase("M512,512l-512,0,0,-512z")]
    [TestCase("M0,0l63,63,64,64,-63,-64,100,-100z")]
    [TestCase("M1.5,2.05l-0,-0.5z")]
    [TestCase("M1,2L3,4H5V6C7,8,9,10,11,12S13,14,15,16Q17,18,19,20T21,22A1,1,0,0,1,23,24ZM0,0z")]
    [TestCase("M1,2m3,4l5,6h7v8c9,10,11,12,13,14s15,16,17,18q19,20,21,22t23,24a1,1,0,0,1,25,26z")]
    [TestCase("M10,10l5-3,-2-7zM300,300l-40,0,0,40z")]
    [TestCase("M007,0100l,-,z")]
    [TestCase("Mz")]
    public void Decode_inverts_encode(string path)
        => Assert.That(OutlineCodec.Decode(OutlineCodec.Encode(path)), Is.EqualTo(path));

    [Test]
    public void Small_numbers_take_one_byte_each_with_their_separator()
    {
        // 10 | ,10 | l | 5 | -3
        Assert.That(OutlineCodec.Encode("M10,10l5-3z"), Is.EqualTo(new byte[] { 10, 128 | 10, 192 + 37, 5, 64 | 3 }));
    }

    [Test]
    public void Numbers_past_63_are_split_into_chunks_that_concatenate_back()
    {
        // ,512 -> ",51" "2"; -300 -> "-30" "0"
        Assert.That(OutlineCodec.Encode("Ml,512-300z"), Is.EqualTo(new byte[] { 192 + 37, 128 | 51, 2, 64 | 30, 0 }));
    }

    [Test]
    public void A_hand_built_vector_decodes_to_telegrams_path()
    {
        byte[] bytes =
        [
            12, 128 | 34,           // 12,34
            192 + 37,               // l
            5, 64 | 3,              // 5-3
            128 | 0, 64 | 63,       // ,0-63
            192 + 51,               // z
            192 + 12,               // M
            1, 192 + 49, 5,         // 1.5
            192 + 63, 192 + 62, 7   // ,-7
        ];

        Assert.That(OutlineCodec.Decode(bytes), Is.EqualTo("M12,34l5-3,0-63zM1.5,-7z"));
    }

    [Test]
    public void Every_table_byte_decodes_to_its_table_character()
    {
        const string table = "AACAAAAHAAALMAAAQASTAVAAAZaacaaaahaaalmaaaqastava.az0123456789-,";

        for (var i = 0; i < 64; i++)
            Assert.That(OutlineCodec.Decode([(byte)(192 + i)]), Is.EqualTo($"M{table[i]}z"));
    }

    [Test]
    public void Every_byte_decodes_and_reencodes_to_the_same_path()
    {
        for (var b = 0; b < 256; b++)
        {
            var path = OutlineCodec.Decode([(byte)b]);
            Assert.That(OutlineCodec.Decode(OutlineCodec.Encode(path)), Is.EqualTo(path), $"byte {b}");
        }
    }

    [Test]
    public void Random_paths_round_trip()
    {
        var random   = new Random(1729);
        const string commands = "MmLlHhVvCcSsQqTtAaZz";

        for (var n = 0; n < 2000; n++)
        {
            var path = new StringBuilder("M");
            path.Append(random.Next(0, 600)).Append(',').Append(random.Next(0, 600));

            var tokens = random.Next(0, 40);
            for (var t = 0; t < tokens; t++)
            {
                switch (random.Next(4))
                {
                    case 0:
                        path.Append(commands[random.Next(commands.Length)]);
                        break;
                    case 1:
                        path.Append('-').Append(random.Next(0, 1000));
                        break;
                    case 2:
                        path.Append(',').Append(random.Next(0, 1000));
                        break;
                    default:
                        path.Append(random.Next(0, 1000));
                        break;
                }
            }

            path.Append('z');
            var text = path.ToString();

            Assert.That(OutlineCodec.Decode(OutlineCodec.Encode(text)), Is.EqualTo(text));
        }
    }

    [TestCase("10,10l5,5z")]
    [TestCase("M10,10l5,5")]
    [TestCase("M10 10l5,5z")]
    [TestCase("M1e3,0z")]
    [TestCase("M+1,0z")]
    public void Paths_the_format_cannot_carry_are_refused(string path)
        => Assert.Throws<ArgumentException>(() => OutlineCodec.Encode(path));
}
