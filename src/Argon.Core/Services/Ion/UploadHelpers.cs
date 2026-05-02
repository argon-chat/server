namespace Argon.Services.Ion;

using ion.runtime;

internal static class UploadHelpers
{
    public static IonArray<FormField> ToFormFields(Dictionary<string, string> fields)
        => new(fields.Select(kv => new FormField(kv.Key, kv.Value)).ToList());
}
