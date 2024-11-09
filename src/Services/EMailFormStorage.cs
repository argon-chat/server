namespace Argon.Api.Services;

using System.Collections.Concurrent;

public class EMailFormStorage
{
    private readonly ConcurrentDictionary<string, string> htmlForms = new();

    public void Load(string name, string content) => htmlForms.TryAdd(name, content);

    public string GetContentFor(string formKey)
    {
        if (htmlForms.TryGetValue(formKey, out var form))
            return form;
        throw new InvalidOperationException($"No '{formKey}' form found");
    }

    public string CompileAndGetForm(string formKey, Dictionary<string, string> values)
    {
        var form = GetContentFor(formKey);

        foreach (var (key, value) in values)
            form = form.Replace($"{{{{{key.ToLowerInvariant()}}}}}", value);
        return form;
    }
}