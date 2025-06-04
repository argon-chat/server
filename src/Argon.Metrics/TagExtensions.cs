namespace Argon.Metrics;

public static class TagExtensions
{
    /// <summary>
        /// Creates a dictionary containing a single key-value pair from the specified key and value.
        /// </summary>
        /// <param name="key">The key for the tag.</param>
        /// <param name="value">The value associated with the key.</param>
        /// <returns>A dictionary with one entry mapping the key to the value.</returns>
        public static IDictionary<string, string> WithTag(this string key, string value)
        => new Dictionary<string, string> { [key] = value };

    /// <summary>
        /// Creates a dictionary from the provided key-value tag pairs.
        /// </summary>
        /// <param name="tags">An array of tuples representing tag keys and values.</param>
        /// <returns>A dictionary containing each tag key mapped to its corresponding value.</returns>
        public static IDictionary<string, string> WithTags(params (string Key, string Value)[] tags)
        => tags.ToDictionary(t => t.Key, t => t.Value);
}