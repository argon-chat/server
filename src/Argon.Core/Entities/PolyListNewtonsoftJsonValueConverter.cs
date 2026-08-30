namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

public class PolyListNewtonsoftJsonValueConverter<T, E>()
    : ValueConverter<T, string>(arg => ToJson(arg), s => FromJson(s))
    where T : IList<E>, new()
{
    private static readonly JsonSerializerSettings _settings = new()
    {
        TypeNameHandling = TypeNameHandling.All,
        Formatting       = Formatting.None,
        Converters       = [new PolymorphicListConverter<E>()]
    };

    // internal rather than private: PolyListJsonValueComparer has to serialize by exactly these
    // settings, and a comparer that disagreed with its converter about $type would report two
    // different element types as the same value.
    internal static string ToJson(T value)
        => JsonConvert.SerializeObject(value, _settings);

    internal static T FromJson(string json)
        => JsonConvert.DeserializeObject<T>(json, _settings) ?? new();
}