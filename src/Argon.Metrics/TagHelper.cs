namespace Argon.Metrics;

public static class TagHelper
{
    public static IDictionary<string, string> ToFlattenTags(object tags)
    {
        var dict = new Dictionary<string, string>();
        foreach (var prop in tags.GetType().GetProperties())
        {
            var value = prop.GetValue(tags)?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
                dict[prop.Name] = value;
        }

        return dict;
    }
}