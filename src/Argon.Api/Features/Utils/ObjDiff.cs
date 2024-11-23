namespace Argon.Features;

using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using ActualLab.Collections;
using ActualLab.Text;

public static class ObjDiff
{
    private static readonly ConcurrentDictionary<Type, object> comparer_cache = new();


    private static bool IsAreEqual(Type t, object t1, object t2)
    {
        var comparerType = typeof(EqualityComparer<>).MakeGenericType(t);

        if (comparer_cache.TryGetValue(t, out var cmp))
        {
            var equalsMethod = comparerType.GetMethod("Equals", [t, t])!;
            return (bool)equalsMethod.Invoke(cmp, new[] { t1, t2 })!;
        }
        else
        {
            var defaultComparer = comparerType.GetProperty("Default")!.GetValue(null)!;

            comparer_cache.TryAdd(t, defaultComparer);

            var equalsMethod = comparerType.GetMethod("Equals", [t, t])!;
            return (bool)equalsMethod.Invoke(defaultComparer, new[] { t1, t2 })!;
        }
    }


    public static PropertyBag Compare<T>(T prev, T next)
    {
        if (EqualityComparer<T>.Default.Equals(prev, next))
            return PropertyBag.Empty;

        var props      = PropertyBag.Empty;
        var properties = typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance);

        foreach (var prop in properties)
        {
            var prevValue    = prop.GetValue(prev)!;
            var updatedValue = prop.GetValue(next)!;

            if (!IsAreEqual(prop.PropertyType, prevValue, updatedValue))
                props.Set(new Symbol(prop.Name), updatedValue);
        }

        return props;
    }
}