namespace Argon;

[JsonObject, Serializable]
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

    [JsonIgnore, IgnoreDataMember]
    public bool IsSuccess => _result is not null;

    [JsonIgnore, IgnoreDataMember]
    public TResult Value => IsSuccess ? _result! : throw new InvalidOperationException("No result available.");

    [JsonIgnore, IgnoreDataMember]
    public TError Error => !IsSuccess ? _error! : throw new InvalidOperationException("No error available.");

    public static Either<TResult, TError> Success(TResult result) => new(result);
    public static Either<TResult, TError> Failure(TError error)   => new(error);

    public static implicit operator Either<TResult, TError>(TResult result) => new(result);
    public static implicit operator Either<TResult, TError>(TError error)   => new(error);
}

[JsonObject, Serializable]
public readonly record struct Maybe<TResult>
{
    [JsonProperty("value")]
    private readonly TResult? _value;

    private Maybe(TResult value) => _value = value;

    [JsonIgnore, IgnoreDataMember]
    public bool HasValue => !EqualityComparer<TResult>.Default.Equals(_value, default);

    [JsonIgnore, IgnoreDataMember]
    public TResult Value => HasValue ? _value! : throw new InvalidOperationException("No value available.");

    public static Maybe<TResult> Some(TResult value) => new(value);
    public static Maybe<TResult> None()              => new();

    public static implicit operator Maybe<TResult>(TResult result) => new(result);
}
