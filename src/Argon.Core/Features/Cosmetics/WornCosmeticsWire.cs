namespace Argon.Features.Cosmetics;

using ion.runtime;
using System.Formats.Cbor;

/// <summary>
/// What a person is wearing, as the bytes the wire carries.
/// </summary>
/// <remarks>
/// <para>For the cache in front of it. The cache's own serializer is System.Text.Json, which sees a
/// union as the interface it is declared as — it would write none of a case's fields and could not
/// read one back — so the entry is encoded with the Ion formatter instead, and a hit costs one
/// decode.</para>
///
/// <para>The list is framed here and each element goes through the union's own formatter. Ion
/// registers formatters for messages and unions, not for a bare list of them: a list is only ever a
/// field of something.</para>
/// </remarks>
public static class WornCosmeticsWire
{
    public static byte[] Write(IonArray<IWornCosmetic> worn)
    {
        var writer = new CborWriter();

        writer.WriteStartArray(worn.Count);

        foreach (var cosmetic in worn)
        {
            IonFormatterStorage<IWornCosmetic>.Write(writer, cosmetic);
        }

        writer.WriteEndArray();

        return writer.Encode();
    }

    public static IonArray<IWornCosmetic> Read(byte[] bytes)
    {
        var reader = new CborReader(bytes);
        var count  = reader.ReadStartArray() ?? 0;
        var worn   = new List<IWornCosmetic>(count);

        for (var at = 0; at < count; at++)
        {
            worn.Add(IonFormatterStorage<IWornCosmetic>.Read(reader));
        }

        reader.ReadEndArray();

        return new IonArray<IWornCosmetic>(worn);
    }
}
