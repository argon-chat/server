namespace Argon.Entities;

using Cassandra.Mapping;

public class MessageEntityConverter : ICassandraConverter<List<IMessageEntity>, string>
{
    private static readonly JsonSerializerSettings _settings = new()
    {
        TypeNameHandling = TypeNameHandling.All,
        Formatting       = Formatting.None,
        Converters       = [new PolymorphicListConverter<IMessageEntity>()]
    };

    public string ConvertTo(List<IMessageEntity> @in)
        => JsonConvert.SerializeObject(@in ?? [], _settings) ?? "[]";

    public List<IMessageEntity> ConvertFrom(string @out)
        => JsonConvert.DeserializeObject<List<IMessageEntity>>(@out ?? "[]", _settings) ?? [];
}

public class DateTimeConverter : ICassandraConverter<DateTimeOffset, long>
{
    public long ConvertTo(DateTimeOffset @in)
        => @in.ToUnixTimeMilliseconds();

    public DateTimeOffset ConvertFrom(long @out)
        => DateTimeOffset.FromUnixTimeMilliseconds(@out);
}

public class DateTimeNullableConverter : ICassandraConverter<DateTimeOffset?, long?>
{
    public long? ConvertTo(DateTimeOffset? @in)
    {
        if (@in is null) return null;
        return @in.Value.ToUnixTimeMilliseconds();
    }

    public DateTimeOffset? ConvertFrom(long? @out)
    {
        if (@out is null) return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(@out.Value);
    }
}