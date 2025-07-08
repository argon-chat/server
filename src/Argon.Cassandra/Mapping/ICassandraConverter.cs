namespace Argon.Cassandra.Mapping;

using System.Diagnostics.CodeAnalysis;

public interface ICassandraConverter
{
    Type FromType { get; }
    Type ToType   { get; }

    object BoxedConvertTo([NotNull] object @in);
    object BoxedConvertFrom([NotNull] object @from);
}

public interface ICassandraConverter<TIn, TOut> : ICassandraConverter
{
    Type ICassandraConverter.FromType => typeof(TIn);
    Type ICassandraConverter.ToType   => typeof(TOut);

    object ICassandraConverter.BoxedConvertTo([NotNull] object @in)
        => (TOut)ConvertTo((TIn)@in)!;

    object ICassandraConverter.BoxedConvertFrom([NotNull] object @from)
        => (TIn)ConvertFrom((TOut)@from)!;

    TOut ConvertTo(TIn @in);
    TIn ConvertFrom(TOut @out);
}