namespace Argon.Services.L1L2;

using ion.runtime;
using Microsoft.Extensions.Caching.Hybrid;
using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Lets a generated Ion contract go into the cache.
/// </summary>
/// <remarks>
/// Ion records are ordinary records except for <see cref="IonArray{T}"/>, which
/// <c>System.Text.Json</c> writes happily and then refuses to read back — it is a read-only
/// collection with no way for the binder to populate it, and the failure surfaces on the first cache
/// hit rather than at the write. Since the alternative is a hand-written flat mirror of every
/// contract that gets cached, and those drift, the array gets a converter instead.
/// </remarks>
public sealed class IonArrayJsonConverter<T> : JsonConverter<IonArray<T>>
{
    public override IonArray<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => new(JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? []);

    public override void Write(Utf8JsonWriter writer, IonArray<T> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value.ToList(), options);
}

public sealed class IonArrayJsonConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(IonArray<>);

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        => (JsonConverter)Activator.CreateInstance(
            typeof(IonArrayJsonConverter<>).MakeGenericType(typeToConvert.GetGenericArguments()[0]))!;
}

/// <summary>
/// Serialises anything that has an <see cref="IonArray{T}"/> somewhere inside it. Everything else is
/// left to whatever HybridCache would have used.
/// </summary>
/// <remarks>
/// Claiming only the shapes that need it is deliberate: overriding the serializer for every type
/// would change how unrelated entries are written, and this is a bug fix for one collection type.
/// </remarks>
public static class IonJson
{
    /// <summary>JSON that can read back what it wrote, Ion contracts included.</summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        Converters = { new IonArrayJsonConverterFactory() }
    };
}

public sealed class IonHybridCacheSerializerFactory : IHybridCacheSerializerFactory
{
    private static readonly JsonSerializerOptions Options = IonJson.Options;

    private static readonly ConcurrentDictionary<Type, bool> Shapes = new();

    public bool TryCreateSerializer<T>([NotNullWhen(true)] out IHybridCacheSerializer<T>? serializer)
    {
        if (!Shapes.GetOrAdd(typeof(T), static type => Walk(type, [])))
        {
            serializer = null;
            return false;
        }

        serializer = new IonSerializer<T>();
        return true;
    }

    /// <summary>
    /// Walks the type looking for an <see cref="IonArray{T}"/>, through generic arguments and public
    /// properties. <paramref name="seen"/> is what stops a self-referencing contract from looping.
    /// </summary>
    /// <remarks>
    /// Only the type asked about is memoised. A type reached partway down may have been cut short by
    /// the cycle guard, and its answer would be wrong the next time it is asked about on its own.
    /// </remarks>
    private static bool Walk(Type type, HashSet<Type> seen)
    {
        if (type.IsPrimitive || type == typeof(string) || type.IsEnum || !seen.Add(type))
            return false;

        if (type.IsGenericType)
        {
            if (type.GetGenericTypeDefinition() == typeof(IonArray<>))
                return true;

            if (type.GetGenericArguments().Any(argument => Walk(argument, seen)))
                return true;
        }

        if (type.IsArray && Walk(type.GetElementType()!, seen))
            return true;

        return type.GetProperties()
           .Where(p => p.GetIndexParameters().Length == 0)
           .Any(p => Walk(p.PropertyType, seen));
    }

    private sealed class IonSerializer<T> : IHybridCacheSerializer<T>
    {
        public T Deserialize(ReadOnlySequence<byte> source)
        {
            var reader = new Utf8JsonReader(source);
            return JsonSerializer.Deserialize<T>(ref reader, Options)!;
        }

        public void Serialize(T value, IBufferWriter<byte> target)
        {
            using var writer = new Utf8JsonWriter(target);
            JsonSerializer.Serialize(writer, value, Options);
        }
    }
}
