namespace Argon;

using MessagePack.Formatters;
using MessagePack.Resolvers;

public class EitherFormatterResolver : IFormatterResolver
{
    public static readonly EitherFormatterResolver Instance = new();

    private EitherFormatterResolver() { }

    public IMessagePackFormatter<T>? GetFormatter<T>()
    {
        var type = typeof(T);
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Either<,>))
        {
            var resultType = type.GetGenericArguments()[0];
            var errorType  = type.GetGenericArguments()[1];

            var formatterType = typeof(EitherFormatter<,>).MakeGenericType(resultType, errorType);
            return (IMessagePackFormatter<T>)Activator.CreateInstance(formatterType)!;
        }

        return StandardResolver.Instance.GetFormatter<T>();
    }
}
public class EitherFormatter<TResult, TError> : IMessagePackFormatter<Either<TResult, TError>>
    where TResult : class
{
    public void Serialize(ref MessagePackWriter writer, Either<TResult, TError> value, MessagePackSerializerOptions options)
    {
        var resolver = options.Resolver;

        writer.WriteMapHeader(3);

        writer.Write(nameof(value.IsSuccess));
        writer.Write(value.IsSuccess);

        writer.Write(nameof(value.Value));
        if (value.IsSuccess)
            resolver.GetFormatterWithVerify<TResult>().Serialize(ref writer, value.Value, options);
        else
            writer.WriteNil();

        writer.Write(nameof(Either<TResult, TError>.Error));
        if (!value.IsSuccess)
            resolver.GetFormatterWithVerify<TError>().Serialize(ref writer, value.Error, options);
        else
            writer.WriteNil();
    }

    public Either<TResult, TError> Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
    {
        var resolver = options.Resolver;

        var count = reader.ReadMapHeader();
        if (count != 3)
            throw new InvalidOperationException("Invalid data format for Either<TResult, TError>.");

        var isSuccess = false;
        TResult? result = null;
        TError? error = default;

        for (var i = 0; i < count; i++)
        {
            var propertyName = reader.ReadString();
            switch (propertyName)
            {
                case nameof(Either<TResult, TError>.IsSuccess):
                isSuccess = reader.ReadBoolean();
                break;
                case nameof(Either<TResult, TError>.Value):
                if (isSuccess)
                    result = resolver.GetFormatterWithVerify<TResult>().Deserialize(ref reader, options);
                else
                    reader.Skip();
                break;

                case nameof(Either<TResult, TError>.Error):
                if (!isSuccess)
                    error = resolver.GetFormatterWithVerify<TError>().Deserialize(ref reader, options);
                else
                    reader.Skip();
                break;

                default:
                reader.Skip(); 
                break;
            }
        }
        return isSuccess
            ? Either<TResult, TError>.Success(result!)
            : Either<TResult, TError>.Failure(error!);
    }
}
[JsonObject, MessagePackObject(true), Serializable]
public record struct Either<TResult, TError> where TResult : class 
{
    private Either(TResult result)
    {
        _result = result;
        _error  = default;
    }

    private Either(TError error)
    {
        _result = null;
        _error  = error;
    }

    internal Either(TResult result, TError error)
    {
        _result = result;
        _error  = error;
    }

    [JsonProperty("result")]
    private TResult? _result { get; init; }

    [JsonProperty("error")]
    private TError? _error { get; init; }

    [JsonIgnore, IgnoreMember]
    public bool IsSuccess => _result is not null;

    [JsonIgnore, IgnoreMember]
    public TResult Value => IsSuccess ? _result! : throw new InvalidOperationException("No result available.");

    [JsonIgnore, IgnoreMember]
    public TError Error => !IsSuccess ? _error! : throw new InvalidOperationException("No error available.");

    public static Either<TResult, TError> Success(TResult result) => new(result);
    public static Either<TResult, TError> Failure(TError error)   => new(error);

    public static implicit operator Either<TResult, TError>(TResult result) => new(result);
    public static implicit operator Either<TResult, TError>(TError error)   => new(error);
}

[JsonObject, MessagePackObject(true), Serializable]
public readonly record struct Maybe<TResult>
{
    [JsonProperty("value"), MessagePack.Key(0)]
    private readonly TResult? _value;

    private Maybe(TResult value) => _value = value;

    [JsonIgnore, IgnoreMember]
    public bool HasValue => !EqualityComparer<TResult>.Default.Equals(_value, default);

    [JsonIgnore, IgnoreMember]
    public TResult Value => HasValue ? _value! : throw new InvalidOperationException("No value available.");

    public static Maybe<TResult> Some(TResult value) => new(value);
    public static Maybe<TResult> None()              => new();

    public static implicit operator Maybe<TResult>(TResult result) => new(result);
}

public static class Either
{
    public static Either<TResult, TError> Value<TResult, TError>(TResult result) where TResult : class => Either<TResult, TError>.Success(result);
    public static Either<TResult, TError> Error<TResult, TError>(TError error) where TResult : class   => Either<TResult, TError>.Failure(error);
}

public static class Maybe
{
    public static Maybe<TResult> Value<TResult>(TResult value) => Maybe<TResult>.Some(value);
    public static Maybe<TResult> None<TResult>()               => Maybe<TResult>.None();
}