namespace Argon.Features.Moderation;

/// <summary>
/// The address and device hashes a report carries, or nothing.
/// </summary>
/// <remarks>
/// <para>An HMAC under the deployment's key rather than a bare digest: <c>SHA256(address)</c> over
/// the four billion IPv4 addresses is a table anyone can build in an afternoon, and that is what
/// reports used to store. With the key held only by the deployment, the hash compares equal for
/// equal inputs — which is all the escalation rule needs — and yields nothing to a reader of the
/// table.</para>
///
/// <para>No key, no hash. A deployment that never configured one stores nothing, and the policy
/// judges independence on accounts alone. "unknown" is the address the server reports for a
/// request that did not come through a trusted edge; hashing it would make every such reporter
/// the same person.</para>
/// </remarks>
public static class ReporterIdentityHasher
{
    public static string? Hash(string? pepper, string? value)
    {
        if (string.IsNullOrWhiteSpace(pepper) || string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();

        if (trimmed.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return null;

        return Convert.ToHexStringLower(
            HMACSHA256.HashData(Encoding.UTF8.GetBytes(pepper), Encoding.UTF8.GetBytes(trimmed)));
    }
}
