namespace Argon.Features.Cosmetics;

using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json.Linq;

/// <summary>
/// A gradient on the wire is a list of ARGB integers, whichever serializer is reading it.
/// </summary>
/// <remarks>
/// <para>Two converters and one shape, which is the whole argument for
/// <see cref="ICosmeticJsonCodec"/> in one file: the console writes a payload through Newtonsoft
/// and the read path parses it through System.Text.Json, and a gradient that round-tripped
/// differently through the two would be a colour that changes when a row is edited.</para>
///
/// <para>A list rather than <see cref="CosmeticGradient"/>'s own three longs, because a JSON number
/// is a double everywhere the client is and two packed colours do not fit in one exactly.</para>
/// </remarks>
internal static class CosmeticGradientWire
{
    internal const string TooMany = "a gradient holds at most six colours";

    internal static bool TryRead(IReadOnlyList<int> colors, out CosmeticGradient? gradient)
    {
        if (colors.Count > CosmeticGradient.MaxStops)
        {
            gradient = null;
            return false;
        }

        gradient = CosmeticGradient.FromList(colors);
        return true;
    }
}

internal sealed class CosmeticGradientStjConverter : JsonConverter<CosmeticGradient>
{
    public override CosmeticGradient Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var colors = JsonSerializer.Deserialize<int[]>(ref reader, options) ?? [];

        if (!CosmeticGradientWire.TryRead(colors, out var gradient))
            throw new JsonException(CosmeticGradientWire.TooMany);

        return gradient!;
    }

    public override void Write(Utf8JsonWriter writer, CosmeticGradient value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value.ToArray(), options);
}

internal sealed class CosmeticGradientNewtonsoftConverter : Newtonsoft.Json.JsonConverter<CosmeticGradient>
{
    public override void WriteJson(JsonWriter writer, CosmeticGradient? value, Newtonsoft.Json.JsonSerializer serializer)
    {
        if (value is null)
        {
            writer.WriteNull();
            return;
        }

        serializer.Serialize(writer, value.ToArray());
    }

    public override CosmeticGradient? ReadJson(
        JsonReader reader,
        Type objectType,
        CosmeticGradient? existingValue,
        bool hasExistingValue,
        Newtonsoft.Json.JsonSerializer serializer)
    {
        if (reader.TokenType is JsonToken.Null)
            return null;

        var colors = JArray.Load(reader).ToObject<int[]>(serializer) ?? [];

        if (!CosmeticGradientWire.TryRead(colors, out var gradient))
            throw new JsonSerializationException(CosmeticGradientWire.TooMany);

        return gradient;
    }
}
