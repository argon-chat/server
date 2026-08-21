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

    private static string ToJson(T value)
        => JsonConvert.SerializeObject(value, _settings);

    private static T FromJson(string json)
        => JsonConvert.DeserializeObject<T>(json, _settings) ?? new();
}