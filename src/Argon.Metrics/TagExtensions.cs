namespace Argon.Metrics;

public static class TagExtensions
{
    public static IDictionary<string, string> WithTag(this string key, string value)
        => new Dictionary<string, string> { [key] = value };

    public static IDictionary<string, string> WithTags(params (string Key, string Value)[] tags)
        => tags.ToDictionary(t => t.Key, t => t.Value);
}