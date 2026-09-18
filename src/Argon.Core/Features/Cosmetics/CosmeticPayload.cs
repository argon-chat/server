namespace Argon.Features.Cosmetics;

using System.ComponentModel.DataAnnotations;

/// <summary>
/// The outcome of checking a catalogue row's payload against its kind's payload type.
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

internal sealed class CosmeticPayloadReport : ICosmeticPayloadReport
{
    private readonly List<string> errors = [];

    public IReadOnlyList<string> Errors => errors;

    public void Error(string message) => errors.Add(message);
}

/// <summary>
/// Deserializes a stored payload into its kind's payload type and reports why it does not fit.
/// </summary>
/// <remarks>
/// <para><c>MissingMemberHandling.Error</c> is deliberate. A payload with a field the type does not
/// declare is almost always a kind that was edited without its catalogue rows being migrated, and
/// silently dropping the field would publish a cosmetic that renders as something other than what
/// the operator filled in.</para>
///
/// <para>The reverse — a declared field the payload omits — is the payload type's own business,
/// expressed with <c>required</c> or <c>[Required]</c>.</para>
/// </remarks>
public static class CosmeticPayloadValidator
{
    private static readonly JsonSerializerSettings Settings = new()
    {
        MissingMemberHandling = MissingMemberHandling.Error,
        NullValueHandling     = NullValueHandling.Include
    };

    public static CosmeticPayloadValidation Validate(Type payloadType, string? payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
            return CosmeticPayloadValidation.Invalid("payload is empty");

        object? payload;

        try
        {
            payload = JsonConvert.DeserializeObject(payloadJson, payloadType, Settings);
        }
        catch (JsonException e)
        {
            return CosmeticPayloadValidation.Invalid(e.Message);
        }

        if (payload is null)
            return CosmeticPayloadValidation.Invalid("payload deserialized to null");

        var annotations = new List<ValidationResult>();

        if (!Validator.TryValidateObject(payload, new ValidationContext(payload), annotations, true))
        {
            var messages = new List<string>(annotations.Count);

            for (var index = 0; index < annotations.Count; index++)
            {
                messages.Add(annotations[index].ErrorMessage ?? "invalid value");
            }

            return new CosmeticPayloadValidation(false, messages);
        }

        if (payload is not IValidatableCosmeticPayload validatable)
            return CosmeticPayloadValidation.Valid;

        var report = new CosmeticPayloadReport();
        validatable.Validate(report);

        return report.Errors.Count == 0
            ? CosmeticPayloadValidation.Valid
            : new CosmeticPayloadValidation(false, report.Errors);
    }
}
