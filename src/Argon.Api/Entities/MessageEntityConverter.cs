namespace Argon.Entities;

using Cassandra.Mapping;
using Newtonsoft.Json;

public class MessageEntityConverter : ICassandraConverter<List<MessageEntity>, string>
{
    private static readonly JsonSerializerSettings _settings = new()
    {
        TypeNameHandling = TypeNameHandling.All,
        Formatting       = Formatting.None,
        Converters       = [new PolymorphicListConverter<MessageEntity>()]
    };

    public string ConvertTo(List<MessageEntity> @in)
        => JsonConvert.SerializeObject(@in ?? [], _settings) ?? "[]";

    public List<MessageEntity> ConvertFrom(string @out)
        => JsonConvert.DeserializeObject<List<MessageEntity>>(@out ?? "[]", _settings) ?? [];
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