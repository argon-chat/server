namespace Argon.Contracts.etc;

using MemoryPack;
using MessagePack;
using Newtonsoft.Json;
using Orleans;

[JsonObject, MessagePackObject, MemoryPackable, Immutable, GenerateSerializer, Serializable]
public readonly partial record struct Either<TResult, TError>
{
    private Either(TResult result)
    {
        _result = result;
        _error  = default;
    }

    private Either(TError error)
    {
        _result = default;
        _error  = error;
    }

    [MemoryPackConstructor]
    internal Either(TResult result, TError error)
    {
        _result = result;
        _error  = error;
    }

    [JsonProperty("result"), Key(0), Id(0), MemoryPackInclude]
    private TResult? _result { get; init; }

    [JsonProperty("error"), Key(1), Id(1), MemoryPackInclude]
    private TError? _error { get; init; }

    [JsonIgnore, IgnoreMember, MemoryPackIgnore]
    public bool IsSuccess => _result is not null;

    [JsonIgnore, IgnoreMember, MemoryPackIgnore]
    public TResult Value => IsSuccess ? _result! : throw new InvalidOperationException("No result available.");

    [JsonIgnore, IgnoreMember, MemoryPackIgnore]
    public TError Error => !IsSuccess ? _error! : throw new InvalidOperationException("No error available.");

    public static Either<TResult, TError> Success(TResult result) => new(result);
    public static Either<TResult, TError> Failure(TError error)   => new(error);

    public static implicit operator Either<TResult, TError>(TResult result) => new(result);
    public static implicit operator Either<TResult, TError>(TError error)   => new(error);
}

//[GenerateSerializer]
//public readonly struct EitherSurrogate<TResult, TError>
//{
//    [Id(0)]
//    public TResult? Result { get; init; }

//    [Id(1)]
//    public TError? Error { get; init; }

//    public static implicit operator EitherSurrogate<TResult, TError>(Either<TResult, TError> either) =>
//        new()  { Result = either.Value, Error = either.Error };

//    public static implicit operator Either<TResult, TError>(EitherSurrogate<TResult, TError> surrogate) =>
//        surrogate.Result is not null ? surrogate.Result : surrogate.Error!;
//}

//[GenerateSerializer]
//public readonly struct MaybeSurrogate<TResult>
//{
//    [Id(0)]
//    public TResult? Value { get; init; }

//    [Id(1)]
//    public bool IsSome { get; init; }

//    public static implicit operator MaybeSurrogate<TResult>(Maybe<TResult> maybe) =>
//        new() { Value = maybe.Value, IsSome = maybe.HasValue };

//    public static implicit operator Maybe<TResult>(MaybeSurrogate<TResult> surrogate) =>
//        surrogate.IsSome ? surrogate.Value : new Maybe<TResult>();
//}

//[RegisterConverter]
//public sealed class EitherConverter<TResult, TError> : IConverter<Either<TResult, TError>, EitherSurrogate<TResult, TError>>
//{
//    public EitherSurrogate<TResult, TError> ConvertToSurrogate(in Either<TResult, TError> value)                => value;
//    public Either<TResult, TError>          ConvertFromSurrogate(in EitherSurrogate<TResult, TError> surrogate) => surrogate;
//}

//[RegisterConverter]
//public sealed class MaybeConverter<TResult> : IConverter<Maybe<TResult>, MaybeSurrogate<TResult>>
//{
//    public MaybeSurrogate<TResult> ConvertToSurrogate(in Maybe<TResult> value)                => value;
//    public Maybe<TResult>          ConvertFromSurrogate(in MaybeSurrogate<TResult> surrogate) => surrogate;
//}

[JsonObject, MessagePackObject, MemoryPackable, Immutable, GenerateSerializer, Serializable]
public readonly partial record struct Maybe<TResult>
{
    [JsonProperty("value"), Key(0), Id(0), MemoryPackInclude]
    private readonly TResult? _value;

    private Maybe(TResult value) => _value = value;

    [JsonIgnore, IgnoreMember, MemoryPackIgnore]
    public bool HasValue => _value is not null;

    [JsonIgnore, IgnoreMember, MemoryPackIgnore]
    public TResult Value => HasValue ? _value! : throw new InvalidOperationException("No value available.");

    public static Maybe<TResult> Some(TResult value) => new(value);
    public static Maybe<TResult> None()              => new();

    public static implicit operator Maybe<TResult>(TResult result) => new(result);
}

public static class Either
{
    public static Either<TResult, TError> Value<TResult, TError>(TResult result) => Either<TResult, TError>.Success(result);
    public static Either<TResult, TError> Error<TResult, TError>(TError error)   => Either<TResult, TError>.Failure(error);
}

public static class Maybe
{
    public static Maybe<TResult> Value<TResult>(TResult value) => Maybe<TResult>.Some(value);
    public static Maybe<TResult> None<TResult>()               => Maybe<TResult>.None();
}