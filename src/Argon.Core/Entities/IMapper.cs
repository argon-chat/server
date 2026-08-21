namespace Argon.Entities;

using ion.runtime;
using System.Linq;

public interface IMapper<TSelf, out ToModel> where TSelf : IMapper<TSelf, ToModel>
{
    abstract static ToModel Map(scoped in TSelf self);

    static ICollection<ToModel> MapCollection(scoped in ICollection<TSelf> selfCollection)
        => selfCollection.Select(x => TSelf.Map(x)).ToList();
}

public static class MapperEx
{
    public static ToModel ToDto<TSelf, ToModel>(this IMapper<TSelf, ToModel> self) where TSelf : IMapper<TSelf, ToModel>
        => TSelf.Map((TSelf)self);
}

public static class IonMaybeExtensions
{
    public static IonMaybe<T> AsMaybe<T>(this T? nullableValue) where T : class
        => nullableValue is null ? IonMaybe<T>.None : IonMaybe<T>.Some(nullableValue);

    public static IonMaybe<T> AsMaybe<T>(this T? nullableValue) where T : struct
        => nullableValue is null ? IonMaybe<T>.None : IonMaybe<T>.Some(nullableValue.Value);
}