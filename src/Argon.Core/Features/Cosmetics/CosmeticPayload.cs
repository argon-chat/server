namespace Argon.Features.Cosmetics;

/// <summary>
/// The outcome of checking a document against the schema its kind declared.
/// </summary>
public readonly record struct CosmeticPayloadValidation(bool IsValid, IReadOnlyList<string> Errors)
{
    public static CosmeticPayloadValidation Valid { get; } = new(true, []);

    public static CosmeticPayloadValidation Invalid(params string[] errors) => new(false, errors);
}

/// <summary>
/// Implemented by a payload type whose rules cannot be expressed as data annotations — a field that
/// is required only when another has a particular value, or a pair that must agree.
/// </summary>
/// <remarks>
/// The same division as <c>IValidatableFeatureOptions</c>: what counts as a valid value belongs to
/// the payload type, not to whoever happens to be saving it. Keeping the rules on the model is what
/// stops one cosmetic's meaning from being split between a kind file and an admin method.
/// </remarks>
public interface IValidatableCosmeticPayload
{
    void Validate(ICosmeticPayloadReport report);
}

public interface ICosmeticPayloadReport
{
    void Error(string message);
}

/// <summary>
/// Implemented by a payload type that a catalogue row carries to clients.
/// </summary>
/// <remarks>
/// The stored document is this build's to read and check; what goes over Ion is the typed case of
/// <see cref="ICosmeticPayload"/>, never the document. Called only on a payload that has already
/// passed its own validation, so every field the schema requires is present.
/// </remarks>
public interface ICosmeticWirePayload
{
    ICosmeticPayload ToWire();
}

internal sealed class CosmeticPayloadReport : ICosmeticPayloadReport
{
    private readonly List<string> errors = [];

    public IReadOnlyList<string> Errors => errors;

    public void Error(string message) => errors.Add(message);
}

/// <summary>
/// One kind's document schema: what a stored payload has to parse into, closed over the type.
/// </summary>
/// <remarks>
/// <para><b>A delegate rather than a <see cref="Type"/>.</b> The kind declares its schema with a
/// generic call, so the one place that knows the type is the one place that can name it — and
/// everything downstream holds a function that checks a string. Nothing in the registry reflects,
/// nothing at render time asks what a payload <i>is</i>, and a definition can be built, compared and
/// logged without dragging the runtime type system along.</para>
/// </remarks>
public sealed class CosmeticPayloadSchema
{
    private readonly Func<string, ICosmeticJsonCodec, CosmeticPayloadValidation> validate;
    private readonly Func<string, ICosmeticJsonCodec, ICosmeticPayload?>        toWire;

    private CosmeticPayloadSchema(
        string name,
        Func<string, ICosmeticJsonCodec, CosmeticPayloadValidation> validate,
        Func<string, ICosmeticJsonCodec, ICosmeticPayload?> toWire)
    {
        Name          = name;
        this.validate = validate;
        this.toWire   = toWire;
    }

    /// <summary>The payload type's name. For diagnostics, and never parsed back.</summary>
    public string Name { get; }

    public static CosmeticPayloadSchema For<TPayload>() where TPayload : class
        => new(typeof(TPayload).Name, Check<TPayload>, Wire<TPayload>);

    public CosmeticPayloadValidation Validate(string? json, ICosmeticJsonCodec? codec = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return CosmeticPayloadValidation.Invalid("payload is empty");

        return validate(json, codec ?? CosmeticJson.Default);
    }

    /// <summary>
    /// The typed case a stored document goes over Ion as, or null when it does not fit its schema or
    /// its type has no case — either of which keeps the row out of what clients are sent.
    /// </summary>
    public ICosmeticPayload? ToWire(string? json, ICosmeticJsonCodec? codec = null)
        => string.IsNullOrWhiteSpace(json) ? null : toWire(json, codec ?? CosmeticJson.Default);

    private static ICosmeticPayload? Wire<TPayload>(string json, ICosmeticJsonCodec codec)
        where TPayload : class
    {
        if (!Check<TPayload>(json, codec).IsValid || !codec.TryRead<TPayload>(json, out var payload, out _))
            return null;

        return (payload as ICosmeticWirePayload)?.ToWire();
    }

    private static CosmeticPayloadValidation Check<TPayload>(string json, ICosmeticJsonCodec codec)
        where TPayload : class
    {
        if (!codec.TryRead<TPayload>(json, out var payload, out var error))
            return CosmeticPayloadValidation.Invalid(error);

        var annotations = new List<ValidationResult>();

        if (!Validator.TryValidateObject(payload, new ValidationContext(payload), annotations, true))
        {
            var messages = new List<string>(annotations.Count);

            foreach (var annotation in annotations)
            {
                messages.Add(annotation.ErrorMessage ?? "invalid value");
            }

            return new CosmeticPayloadValidation(false, messages);
        }

        if (payload is not IValidatableCosmeticPayload validatable)
            return CosmeticPayloadValidation.Valid;

        var report = new CosmeticPayloadReport();
        validatable.Validate(report);

        return report.Errors.Count is 0
            ? CosmeticPayloadValidation.Valid
            : new CosmeticPayloadValidation(false, report.Errors);
    }
}
