namespace Argon.Features.BotApi;

using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.Extensions.Primitives;
using JsonOptions = Microsoft.AspNetCore.Http.Json.JsonOptions;
using StjSerializer = System.Text.Json.JsonSerializer;
using StjException = System.Text.Json.JsonException;

/// <summary>
/// A file handed to a form route (Telegram's <c>InputFile</c>): a part of the request, or the id of an earlier
/// upload. A field writes it as <c>attach://&lt;part&gt;</c> or as the id; a part with the field's own name is the file itself.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(BotInputFileJsonConverter))]
public sealed class BotInputFile
{
    public const string AttachScheme = "attach://";

    public Guid?   FileId      { get; private init; }
    public string? Part        { get; private init; }
    public byte[]? Data        { get; private set; }
    public string? FileName    { get; private set; }
    public string? ContentType { get; private set; }

    public static BotInputFile FromFileId(Guid fileId) => new() { FileId = fileId };

    public static BotInputFile FromPart(string part) => new() { Part = part };

    public static BotInputFile FromBytes(byte[] data, string? fileName = null, string? contentType = null)
        => new() { Data = data, FileName = fileName, ContentType = contentType };

    internal void Load(byte[] data, string? fileName, string? contentType)
    {
        Data        = data;
        FileName    = fileName;
        ContentType = contentType;
    }

    public override string ToString() => FileId?.ToString() ?? $"{AttachScheme}{Part}";
}

/// <summary>Marks a <see cref="BotInputFile"/> field that takes the bytes only — a part or <c>attach://</c>, never a fileId.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class BotPartOnlyAttribute : Attribute;

public sealed class BotInputFileJsonConverter : System.Text.Json.Serialization.JsonConverter<BotInputFile>
{
    public override BotInputFile Read(ref System.Text.Json.Utf8JsonReader reader, Type typeToConvert, System.Text.Json.JsonSerializerOptions options)
    {
        var text = reader.TokenType == System.Text.Json.JsonTokenType.String ? reader.GetString() : null;

        if (text is not null && text.StartsWith(BotInputFile.AttachScheme, StringComparison.OrdinalIgnoreCase)
         && text.Length > BotInputFile.AttachScheme.Length)
            return BotInputFile.FromPart(text[BotInputFile.AttachScheme.Length..]);

        if (Guid.TryParse(text, out var fileId))
            return BotInputFile.FromFileId(fileId);

        throw new StjException("A file is given as attach://<part> or as the fileId of an upload.");
    }

    public override void Write(System.Text.Json.Utf8JsonWriter writer, BotInputFile value, System.Text.Json.JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

/// <summary>What a custom-bound route parameter carries into the endpoint filters.</summary>
public interface IBotBoundRequest
{
    object?          Value { get; }
    BotApiException? Error { get; }
}

/// <summary>
/// The request of a form route. Binding never fails the parameter itself: an error is carried to
/// <see cref="BotFormFilter"/>, which runs inside <see cref="BotErrorFilter"/> and so answers with a declared error.
/// </summary>
public sealed class BotForm<T> : IBotBoundRequest where T : notnull
{
    public T?               Value { get; private init; }
    public BotApiException? Error { get; private init; }

    object? IBotBoundRequest.Value => Value;

    public static async ValueTask<BotForm<T>?> BindAsync(HttpContext ctx, ParameterInfo parameter)
    {
        var limit = ctx.GetEndpoint()?.Metadata.GetMetadata<IRequestSizeLimitMetadata>()?.MaxRequestBodySize ?? BotFormBinder.DefaultLimit;

        try
        {
            return new BotForm<T> { Value = await BotFormBinder.BindAsync<T>(ctx, limit) };
        }
        catch (BotApiException e)
        {
            return new BotForm<T> { Error = e };
        }
    }
}

/// <summary>
/// The request of a route that takes JSON or a form: a form body goes to <see cref="BotFormBinder"/>, anything else is
/// read as JSON. Errors travel as they do for <see cref="BotForm{T}"/>.
/// </summary>
public sealed class BotBodyOrForm<T> : IBotBoundRequest where T : notnull
{
    public T?               Value { get; private init; }
    public BotApiException? Error { get; private init; }

    object? IBotBoundRequest.Value => Value;

    public static async ValueTask<BotBodyOrForm<T>?> BindAsync(HttpContext ctx, ParameterInfo parameter)
    {
        if (ctx.Request.HasFormContentType)
        {
            var form = await BotForm<T>.BindAsync(ctx, parameter);
            return new BotBodyOrForm<T> { Value = form!.Value, Error = form.Error };
        }

        var options = ctx.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions ?? System.Text.Json.JsonSerializerOptions.Web;

        try
        {
            return await StjSerializer.DeserializeAsync<T>(ctx.Request.Body, options, ctx.RequestAborted) is { } value
                ? new BotBodyOrForm<T> { Value = value }
                : new BotBodyOrForm<T> { Error = BotErrors.InvalidRequest.Raise("The body is empty.") };
        }
        catch (StjException e)
        {
            return new BotBodyOrForm<T>
            {
                Error = BotErrors.InvalidRequest.Raise(e.Path is { Length: > 1 } path ? $"{path.TrimStart('$', '.')} is malformed." : e.Message)
            };
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            return new BotBodyOrForm<T> { Error = BotErrors.TooLarge.Raise() };
        }
    }
}

/// <summary>Answers a form that could not be bound, before the membership check has nothing to read.</summary>
public sealed class BotFormFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        foreach (var argument in context.Arguments)
            if (argument is IBotBoundRequest { Error: { } error })
                throw error;

        return await next(context);
    }
}

public sealed record BotRequestSizeLimit(long? MaxRequestBodySize) : IRequestSizeLimitMetadata;

/// <summary>
/// Binds a multipart (or url-encoded) form to a request record, the way Telegram's Bot API reads one: every field
/// is a string, turned into the property's type as JSON would be — numbers, booleans and <c>[…]</c> arrays as
/// JSON, anything else as a string — and a repeated field fills a list. <see cref="BotInputFile"/> properties
/// take a part: the one named like the field, or the one an <c>attach://</c> reference names.
/// </summary>
public static class BotFormBinder
{
    public const long DefaultLimit = 5 * 1024 * 1024;

    public static async Task<T> BindAsync<T>(HttpContext ctx, long limit) where T : notnull
    {
        var request = ctx.Request;

        if (!request.HasFormContentType)
            throw BotErrors.InvalidRequest.Raise("Send the request as multipart/form-data.");
        if (request.ContentLength > limit)
            throw BotErrors.TooLarge.Raise($"The request is over {limit} bytes.");

        if (ctx.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } bodySize)
            bodySize.MaxRequestBodySize = limit;

        ctx.Features.Set<IFormFeature>(new FormFeature(request, new FormOptions
        {
            MultipartBodyLengthLimit = limit,
            ValueCountLimit          = 256,
            ValueLengthLimit         = 64 * 1024
        }));

        IFormCollection form;
        try
        {
            form = await request.ReadFormAsync(ctx.RequestAborted);
        }
        catch (BadHttpRequestException e) when (e.StatusCode == StatusCodes.Status413PayloadTooLarge)
        {
            throw BotErrors.TooLarge.Raise($"The request is over {limit} bytes.");
        }
        catch (InvalidDataException e) when (e.Message.Contains("length limit", StringComparison.OrdinalIgnoreCase))
        {
            throw BotErrors.TooLarge.Raise($"The request is over {limit} bytes.");
        }
        catch (InvalidDataException e)
        {
            throw BotErrors.InvalidRequest.Raise(e.Message);
        }

        if (form.Files.Sum(f => f.Length) > limit)
            throw BotErrors.TooLarge.Raise($"The request is over {limit} bytes.");

        var options = ctx.RequestServices.GetService<IOptions<JsonOptions>>()?.Value.SerializerOptions ?? System.Text.Json.JsonSerializerOptions.Web;
        return await BindAsync<T>(form, options, ctx.RequestAborted);
    }

    /// <summary>The binding itself, for a form already read.</summary>
    public static async Task<T> BindAsync<T>(IFormCollection form, System.Text.Json.JsonSerializerOptions options, CancellationToken ct = default)
        where T : notnull
    {
        var json   = new JsonObject();
        var inputs = new List<PropertyInfo>();

        foreach (var property in typeof(T).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var name = options.PropertyNamingPolicy?.ConvertName(property.Name) ?? property.Name;

            if (property.PropertyType == typeof(BotInputFile))
            {
                inputs.Add(property);

                if (form.Files.GetFile(name) is not null)
                    json[name] = BotInputFile.AttachScheme + name;
                else if (form.TryGetValue(name, out var reference))
                    json[name] = reference[^1];
                else if (IsRequired(property))
                    throw BotErrors.InvalidRequest.Raise($"{name} is required: a part of that name, attach://<part> or a fileId.");

                continue;
            }

            // A list of files: references and fileIds as any list is given, then every part named like the field.
            if (ElementType(property.PropertyType) == typeof(BotInputFile))
            {
                inputs.Add(property);

                var files = form.TryGetValue(name, out var references) && references.Count > 0
                    ? Node(references, property.PropertyType) as JsonArray ?? []
                    : [];

                foreach (var _ in form.Files.GetFiles(name))
                    files.Add(BotInputFile.AttachScheme + name);

                if (files.Count > 0)
                    json[name] = files;
                else if (IsRequired(property))
                    throw BotErrors.InvalidRequest.Raise($"{name} is required.");

                continue;
            }

            if (form.TryGetValue(name, out var values) && values.Count > 0)
                json[name] = Node(values, property.PropertyType);
            else if (IsRequired(property))
                throw BotErrors.InvalidRequest.Raise($"{name} is required.");
        }

        T value;
        try
        {
            value = StjSerializer.Deserialize<T>(json, options) ?? throw BotErrors.InvalidRequest.Raise();
        }
        catch (StjException e)
        {
            throw BotErrors.InvalidRequest.Raise(e.Path is { } path ? $"{path.TrimStart('$', '.')} is malformed." : e.Message);
        }

        // A part name given more than once takes its parts in order, the last one again once they run out.
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var property in inputs)
        {
            IEnumerable<BotInputFile> files = property.GetValue(value) switch
            {
                BotInputFile one                => [one],
                IEnumerable<BotInputFile?> many => many.OfType<BotInputFile>(),
                _                               => []
            };

            foreach (var file in files)
            {
                if (file.Part is not { } part)
                    continue;

                var parts = form.Files.GetFiles(part);
                if (parts.Count == 0)
                    throw BotErrors.InvalidRequest.Raise($"{BotInputFile.AttachScheme}{part} names no part of the request.");

                var index  = taken[part] = taken.GetValueOrDefault(part, -1) + 1;
                var source = parts[Math.Min(index, parts.Count - 1)];
                var data   = new byte[source.Length];

                await using (var stream = source.OpenReadStream())
                    await stream.ReadExactlyAsync(data, ct);

                file.Load(data, source.FileName, source.ContentType);
            }
        }

        return value;
    }

    private static JsonNode? Node(StringValues values, Type type)
    {
        var target = Nullable.GetUnderlyingType(type) ?? type;

        if (target != typeof(string) && ElementType(target) is { } element)
        {
            // One field holding a JSON array, or the field repeated once per element.
            if (values.Count == 1 && values[0]?.TrimStart().StartsWith('[') == true && TryParse(values[0]!) is JsonArray array)
                return array;

            var list = new JsonArray();
            foreach (var value in values)
                list.Add(Scalar(value ?? string.Empty, element));
            return list;
        }

        return Scalar(values[^1] ?? string.Empty, target);
    }

    private static JsonNode? Scalar(string value, Type type)
        => type == typeof(string) || Nullable.GetUnderlyingType(type) == typeof(string)
            ? JsonValue.Create(value)
            : TryParse(value) ?? JsonValue.Create(value);

    private static JsonNode? TryParse(string value)
    {
        try
        {
            return JsonNode.Parse(value);
        }
        catch (StjException)
        {
            return null;
        }
    }

    private static Type? ElementType(Type type)
        => type.IsArray
            ? type.GetElementType()
            : type.IsGenericType && type.GetInterfaces().Append(type).FirstOrDefault(i => i.IsGenericType
                                                                                   && i.GetGenericTypeDefinition() == typeof(IEnumerable<>)) is { } enumerable
                ? enumerable.GetGenericArguments()[0]
                : null;

    /// <summary>A constructor parameter with no default, of a type that cannot be null.</summary>
    private static bool IsRequired(PropertyInfo property)
    {
        var parameter = property.DeclaringType?.GetConstructors()
           .SelectMany(c => c.GetParameters())
           .FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase));

        if (parameter is null || parameter.HasDefaultValue)
            return false;
        if (property.PropertyType.IsValueType)
            return Nullable.GetUnderlyingType(property.PropertyType) is null;

        // NullabilityInfoContext is not thread-safe.
        return new NullabilityInfoContext().Create(parameter).WriteState is NullabilityState.NotNull;
    }
}
