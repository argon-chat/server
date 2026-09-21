namespace Argon.Features.Cosmetics;

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Newtonsoft.Json.Linq;

/// <summary>
/// Reads a stored cosmetic document, whichever serializer the caller lives in.
/// </summary>
/// <remarks>
/// <para><b>Two serializers because the process has two.</b> Entity value converters and the admin
/// console are Newtonsoft; anything newer is System.Text.Json. A payload read through one and
/// written through the other has to mean the same thing, and the only way to be sure of that is for
/// both to go through one interface with one set of rules and one set of tests.</para>
///
/// <para>The rule both enforce is that an unknown member is an error. A payload carrying a field its
/// type does not declare is almost always a kind edited without its catalogue rows being migrated,
/// and dropping the field silently publishes a cosmetic that renders as something other than what
/// the operator filled in.</para>
/// </remarks>
public interface ICosmeticJsonCodec
{
    string Name { get; }

    bool TryRead<T>(string json, [NotNullWhen(true)] out T? value, [NotNullWhen(false)] out string? error)
        where T : class;

    string Write<T>(T value) where T : class;
}

public static class CosmeticJson
{
    public static ICosmeticJsonCodec SystemText { get; } = new SystemTextJsonCodec();
    public static ICosmeticJsonCodec Newtonsoft { get; } = new NewtonsoftCodec();

    /// <summary>What validation uses when a caller does not care.</summary>
    public static ICosmeticJsonCodec Default => SystemText;

    private sealed class SystemTextJsonCodec : ICosmeticJsonCodec
    {
        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy        = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            UnmappedMemberHandling      = JsonUnmappedMemberHandling.Disallow,
            DefaultIgnoreCondition      = JsonIgnoreCondition.WhenWritingNull,
            Converters                  = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
        };

        public string Name => "System.Text.Json";

        public bool TryRead<T>(string json, [NotNullWhen(true)] out T? value, [NotNullWhen(false)] out string? error)
            where T : class
        {
            try
            {
                value = JsonSerializer.Deserialize<T>(json, Options);
            }
            catch (JsonException problem)
            {
                value = null;
                error = problem.Message;
                return false;
            }
            // A polymorphic base handed an object with no discriminator at all comes back as this
            // rather than as a JsonException, and it is the same class of problem: a document that
            // does not say what it is.
            catch (NotSupportedException problem)
            {
                value = null;
                error = problem.Message;
                return false;
            }

            error = value is null ? "payload deserialized to null" : null;
            return value is not null;
        }

        public string Write<T>(T value) where T : class => JsonSerializer.Serialize(value, Options);
    }

    private sealed class NewtonsoftCodec : ICosmeticJsonCodec
    {
        private static readonly JsonSerializerSettings Settings = new()
        {
            MissingMemberHandling = MissingMemberHandling.Error,
            NullValueHandling     = NullValueHandling.Ignore,
            ContractResolver      = new Newtonsoft.Json.Serialization.CamelCasePropertyNamesContractResolver(),
            Converters =
            {
                new CosmeticFramePartConverter(),
                new CosmeticGradientNewtonsoftConverter(),
                new StringEnumConverter(new Newtonsoft.Json.Serialization.CamelCaseNamingStrategy())
            }
        };

        public string Name => "Newtonsoft.Json";

        public bool TryRead<T>(string json, [NotNullWhen(true)] out T? value, [NotNullWhen(false)] out string? error)
            where T : class
        {
            try
            {
                value = JsonConvert.DeserializeObject<T>(json, Settings);
            }
            catch (Newtonsoft.Json.JsonException problem)
            {
                value = null;
                error = problem.Message;
                return false;
            }

            error = value is null ? "payload deserialized to null" : null;
            return value is not null;
        }

        public string Write<T>(T value) where T : class => JsonConvert.SerializeObject(value, Settings);
    }
}

/// <summary>
/// Teaches Newtonsoft the discriminator System.Text.Json reads from an attribute.
/// </summary>
/// <remarks>
/// Deliberately not <c>TypeNameHandling</c>, which resolves an arbitrary assembly-qualified name out
/// of a column an operator can write to. The map below is the whole of what a <c>type</c> may say.
/// </remarks>
internal sealed class CosmeticFramePartConverter : Newtonsoft.Json.JsonConverter
{
    private const string Discriminator = "type";

    /// <summary>
    /// The base and only the base.
    /// </summary>
    /// <remarks>
    /// Written against the untyped converter rather than <c>JsonConverter&lt;T&gt;</c>, whose
    /// <c>CanConvert</c> is sealed to every type assignable to <c>T</c> — which here includes the
    /// three subtypes, so the <c>ToObject</c> below would come straight back through this method,
    /// find the discriminator already removed, and refuse a document that was perfectly good.
    /// </remarks>
    public override bool CanConvert(Type objectType) => objectType == typeof(CosmeticFramePart);

    public override bool CanWrite => false;

    public override void WriteJson(JsonWriter writer, object? value, Newtonsoft.Json.JsonSerializer serializer)
        => throw new NotSupportedException();

    public override object? ReadJson(
        JsonReader reader,
        Type objectType,
        object? existingValue,
        Newtonsoft.Json.JsonSerializer serializer)
    {
        if (reader.TokenType is JsonToken.Null)
            return null;

        var part = JObject.Load(reader);

        if (part[Discriminator]?.Value<string>() is not { Length: > 0 } named)
            throw new JsonSerializationException($"a frame part carries no '{Discriminator}'");

        if (!CosmeticFramePart.Shapes.TryGetValue(named, out var shape))
            throw new JsonSerializationException($"'{named}' is not a frame part; it is one of {CosmeticFramePart.ShapeNames}");

        // The discriminator is not a member of the subtype, and MissingMemberHandling.Error would
        // refuse it as one.
        part.Remove(Discriminator);

        return part.ToObject(shape, serializer);
    }
}
