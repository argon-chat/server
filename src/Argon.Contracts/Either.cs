namespace Argon;

[JsonObject, MessagePackObject(true), Serializable]
public readonly record struct Either<TResult, TError> where TResult : class
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

    [JsonProperty("result"), MessagePack.Key(0)]
    private TResult? _result { get; init; }

    [JsonProperty("error"), MessagePack.Key(1)]
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

[JsonObject, MessagePackObject, Serializable]
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