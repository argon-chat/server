namespace Argon.Entities;

using Microsoft.EntityFrameworkCore.ChangeTracking;

/// <summary>
/// How EF decides whether a JSON-persisted collection has changed.
/// </summary>
/// <remarks>
/// <para>A property with a value converter and no comparer is tracked by reference. For a collection
/// that is exactly wrong: the reference never changes, so mutating one in place — adding a reaction,
/// appending an entity, editing a score — leaves the entity looking untouched and
/// <c>SaveChanges</c> writes nothing. The failure is silent and it is a lost write, which is why EF
/// warns about every one of them at model build.</para>
///
/// <para>Equality is decided on the serialized form rather than element by element. That is not a
/// shortcut: the serialized form is what actually reaches the column, so two values that serialize
/// the same ARE the same as far as the database is concerned, and two that do not are not — even
/// when the elements would compare equal, as with a polymorphic list whose <c>$type</c> changed.</para>
///
/// <para>The snapshot round-trips through JSON, which gives change tracking a genuinely detached
/// copy. A shallow copy would leave the original elements shared with the snapshot, and an in-place
/// edit would mutate both — comparing a value against itself and finding no change, which is the
/// same lost write by a longer route.</para>
/// </remarks>
public sealed class JsonValueComparer<T>() : ValueComparer<T>(
    (left, right) => JsonConvert.SerializeObject(left) == JsonConvert.SerializeObject(right),
    value => JsonConvert.SerializeObject(value).GetHashCode(),
    value => JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value))!);

/// <summary>
/// The comparer for the properties converted by <see cref="PolyListNewtonsoftJsonValueConverter{T,E}"/>.
/// </summary>
/// <remarks>
/// Separate from <see cref="JsonValueComparer{T}"/> because it has to serialize the way that
/// converter does — <c>TypeNameHandling.All</c> and the polymorphic element converter. A comparer
/// that used the default settings would drop <c>$type</c> from its comparison, and two lists holding
/// different implementations of the same interface with identical fields would compare equal, so a
/// change of element type would not be written.
/// </remarks>
public sealed class PolyListJsonValueComparer<T, E>() : ValueComparer<T>(
    (left, right) => PolyListNewtonsoftJsonValueConverter<T, E>.ToJson(left) ==
                     PolyListNewtonsoftJsonValueConverter<T, E>.ToJson(right),
    value => PolyListNewtonsoftJsonValueConverter<T, E>.ToJson(value).GetHashCode(),
    value => PolyListNewtonsoftJsonValueConverter<T, E>.FromJson(
        PolyListNewtonsoftJsonValueConverter<T, E>.ToJson(value)))
    where T : IList<E>, new();
