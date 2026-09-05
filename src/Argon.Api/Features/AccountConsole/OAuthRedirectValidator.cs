namespace Argon.Api.Features.AccountConsole;

using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;

/// <summary>
/// One rule about an OAuth redirect URI. Returns the reason it is unacceptable, or <c>null</c> when
/// it passes.
/// </summary>
public abstract class OAuthRedirectValidator
{
    public abstract Task<string?> ValidateAsync(string rawRedirect);
}

/// <summary>
/// The full rule set applied to a redirect before it is written to an app.
/// </summary>
/// <remarks>
/// Every rule re-parses the URI and returns <c>null</c> when it cannot, so a malformed URI is
/// reported once — by <see cref="BasicFormatValidator"/> — instead of by every rule in turn.
/// </remarks>
public sealed class CompositeOAuthRedirectValidator : OAuthRedirectValidator
{
    public static CompositeOAuthRedirectValidator ValidatorForOAuthApps()
        => new CompositeOAuthRedirectValidator()
           .Add(new BasicFormatValidator())
           .Add(new SchemeValidator())
           .Add(new HostValidator())
           .Add(new PathValidator())
           .Add(new QueryValidator())
           .Add(new TldValidator())
           .Add(new SslCertificateValidator())
           .Add(new DomainWildcardDepthValidator(4));

    private readonly List<OAuthRedirectValidator> validators = [];

    public CompositeOAuthRedirectValidator Add(OAuthRedirectValidator validator)
    {
        validators.Add(validator);
        return this;
    }

    public async override Task<string?> ValidateAsync(string rawRedirect)
    {
        foreach (var validator in validators)
        {
            var error = await validator.ValidateAsync(rawRedirect);

            if (!string.IsNullOrEmpty(error))
                return error;
        }

        return null;
    }
}

/// <summary>
/// The rule set for an application that runs on somebody's device rather than on a server.
/// </summary>
/// <remarks>
/// A desktop or mobile client has nowhere to receive a redirect on the web, so RFC 8252 gives it two
/// other ways home: a loopback address, which the web rules already accept, and a private-use scheme
/// of its own. The latter is a URI no web rule can judge — it has no TLD to weigh, no certificate to
/// dial — so it is judged on its own terms and everything else falls through to the web rules
/// unchanged.
/// <para>
/// What makes accepting them safe is not this validator but the check at authorization time, which
/// compares the incoming <c>redirect_uri</c> against the registered list exactly. This decides what
/// may be written to that list; nothing here is pattern-matched later.
/// </para>
/// </remarks>
public sealed class NativeAppRedirectValidator(OAuthRedirectValidator webValidator) : OAuthRedirectValidator
{
    public static NativeAppRedirectValidator ForNativeApps()
        => new(CompositeOAuthRedirectValidator.ValidatorForOAuthApps());

    private static readonly OAuthRedirectValidator Format = new BasicFormatValidator();
    private static readonly OAuthRedirectValidator Path   = new PathValidator();
    private static readonly OAuthRedirectValidator Query  = new QueryValidator();

    private static readonly string[] Forbidden =
        ["file", "ftp", "javascript", "ws", "wss", "data", "chrome", "blob", "about", "vbscript", "intent"];

    public async override Task<string?> ValidateAsync(string rawRedirect)
    {
        if (await Format.ValidateAsync(rawRedirect) is { Length: > 0 } malformed)
            return malformed;

        if (!Uri.TryCreate(rawRedirect, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme is "http" or "https")
            return await webValidator.ValidateAsync(rawRedirect);

        if (ValidatePrivateUseScheme(uri) is { Length: > 0 } badScheme)
            return badScheme;

        // A native redirect commonly has no path at all — gl.argon.app://callback is the whole of
        // it — and the rule that insists on one is a rule about web addresses.
        if (uri.AbsolutePath.Length > 0 && await Path.ValidateAsync(rawRedirect) is { Length: > 0 } badPath)
            return badPath;

        return await Query.ValidateAsync(rawRedirect);
    }

    /// <remarks>
    /// The dot is RFC 8252 §7.1: a private-use scheme has to be a domain the developer controls,
    /// spelled backwards. It costs nothing to type and it is the only thing standing between an
    /// application and registering <c>mail:</c>.
    /// </remarks>
    private static string? ValidatePrivateUseScheme(Uri uri)
    {
        var scheme = uri.Scheme;

        if (Forbidden.Contains(scheme, StringComparer.OrdinalIgnoreCase))
            return $"Scheme '{scheme}' is forbidden.";

        if (!scheme.Contains('.'))
            return $"Private-use scheme '{scheme}' must be a domain you control in reverse order, e.g. 'gl.argon.app'.";

        if (!Regex.IsMatch(scheme, "^[a-z][a-z0-9+.-]*$"))
            return $"Private-use scheme '{scheme}' is not a valid URI scheme.";

        var host = uri.Host;

        if (host.Contains('*'))
            return "Wildcard hosts forbidden.";

        if (host.Contains('@'))
            return "Host must not contain '@'.";

        return host.Any(c => c > 127) ? "Unicode hostnames forbidden." : null;
    }
}

public sealed class BasicFormatValidator : OAuthRedirectValidator
{
    public override Task<string?> ValidateAsync(string rawRedirect)
        => Task.FromResult(Validate(rawRedirect));

    private static string? Validate(string rawRedirect)
    {
        if (string.IsNullOrWhiteSpace(rawRedirect))
            return "Redirect URI is empty.";

        if (rawRedirect.Length > 2048)
            return "Redirect URI is too long.";

        if (!Uri.TryCreate(rawRedirect, UriKind.Absolute, out _))
            return "Invalid URI format.";

        return rawRedirect.Contains('\0') ? "Null bytes forbidden." : null;
    }
}

public sealed class SchemeValidator(bool allowHttpLocalhost = true) : OAuthRedirectValidator
{
    private static readonly string[] Forbidden = ["file", "ftp", "javascript", "ws", "wss", "data", "chrome"];

    public override Task<string?> ValidateAsync(string rawRedirect)
        => Task.FromResult(Validate(rawRedirect));

    private string? Validate(string rawRedirect)
    {
        if (!Uri.TryCreate(rawRedirect, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            var isLocal =
                allowHttpLocalhost &&
                uri.Scheme == Uri.UriSchemeHttp &&
                (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                 uri.Host == "127.0.0.1" ||
                 uri.Host == "::1");

            if (!isLocal)
                return $"Only HTTPS allowed. Scheme '{uri.Scheme}' forbidden.";
        }

        return Forbidden.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase)
            ? $"Scheme '{uri.Scheme}' is forbidden."
            : null;
    }
}

public sealed class HostValidator : OAuthRedirectValidator
{
    public override Task<string?> ValidateAsync(string rawRedirect)
        => Task.FromResult(Validate(rawRedirect));

    private static string? Validate(string rawRedirect)
    {
        if (!Uri.TryCreate(rawRedirect, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host;

        if (string.IsNullOrWhiteSpace(host))
            return "Host is empty.";

        if (host.Contains('*'))
            return "Wildcard hosts forbidden.";

        // Punycode and raw unicode are both refused: a homograph domain that renders like a legitimate
        // one is exactly the shape a redirect allow-list must not accept.
        if (host.StartsWith("xn--"))
            return "Punycode hostnames forbidden.";

        if (host.Any(c => c > 127))
            return "Unicode hostnames forbidden.";

        if (host.Contains('@'))
            return "Host must not contain '@'.";

        return host == "0.0.0.0" ? "0.0.0.0 forbidden." : null;
    }
}

public sealed class PathValidator : OAuthRedirectValidator
{
    private static readonly string[] ForbiddenSegments = [".git", "admin", "internal", "config", "system"];

    public override Task<string?> ValidateAsync(string rawRedirect)
        => Task.FromResult(Validate(rawRedirect));

    private static string? Validate(string rawRedirect)
    {
        if (!Uri.TryCreate(rawRedirect, UriKind.Absolute, out var uri))
            return null;

        var path = uri.AbsolutePath;

        if (path.Length == 0)
            return "Path must not be empty.";

        if (path == "/")
            return null;

        if (path.Contains(".."))
            return "Path traversal forbidden.";

        if (path.Contains("//"))
            return "Double slashes forbidden.";

        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
            if (ForbiddenSegments.Contains(segment, StringComparer.OrdinalIgnoreCase))
                return $"Forbidden path segment '{segment}'.";

        return path.Any(c => c > 127) ? "Non-ASCII path forbidden." : null;
    }
}

public sealed class QueryValidator : OAuthRedirectValidator
{
    public override Task<string?> ValidateAsync(string rawRedirect)
        => Task.FromResult(Validate(rawRedirect));

    private static string? Validate(string rawRedirect)
    {
        if (!Uri.TryCreate(rawRedirect, UriKind.Absolute, out var uri))
            return null;

        var query = uri.Query;

        if (query == "?")
            return "Empty query forbidden.";

        if (query.Contains("%00"))
            return "Null-byte encoding forbidden.";

        if (!query.Contains('%'))
            return null;

        return Regex.Matches(query, "%[0-9A-Fa-f]{2}").Count != query.Count(c => c == '%')
            ? "Invalid %-encoding."
            : null;
    }
}

public sealed class TldValidator : OAuthRedirectValidator
{
    private static readonly HashSet<string> ForbiddenTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "local", "lan", "corp", "internal", "test", "invalid", "example"
    };

    private static readonly HashSet<string> HighRiskTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "zip", "mov", "xin", "qpon", "locker", "lgbt", "top", "xyz", "icu", "pw", "zw", "ke",
        "date", "cd", "bid", "ml", "cf", "ga", "sbs", "rest", "win", "info", "loan", "men",
        "monster", "shop", "cc"
    };

    public override Task<string?> ValidateAsync(string rawRedirect)
        => Task.FromResult(Validate(rawRedirect));

    private static string? Validate(string rawRedirect)
    {
        if (!Uri.TryCreate(rawRedirect, UriKind.Absolute, out var uri))
            return null;

        var host = uri.Host;

        if (host.Equals("localhost"))
            return null;

        var labels = host.Split('.');

        if (labels.Length < 2)
            return "Host must contain a valid TLD.";

        var tld = labels[^1];

        if (ForbiddenTlds.Contains(tld))
            return $"TLD '{tld}' is forbidden.";

        if (HighRiskTlds.Contains(tld))
            return $"TLD '{tld}' is considered high-risk and forbidden.";

        return labels is [{ Length: 0 }, _] ? "Redirect URI cannot point directly to a public suffix." : null;
    }
}

/// <summary>
/// Dials the host and refuses a redirect whose certificate does not currently validate.
/// </summary>
/// <remarks>
/// This is the one rule that touches the network, which is why redirect validation runs on the
/// console rather than in the grain that stores the result.
/// </remarks>
public sealed class SslCertificateValidator : OAuthRedirectValidator
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    public async override Task<string?> ValidateAsync(string rawRedirect)
    {
        if (!Uri.TryCreate(rawRedirect, UriKind.Absolute, out var uri))
            return null;

        if (uri.Scheme != Uri.UriSchemeHttps)
            return null;

        using var timeout = new CancellationTokenSource(HandshakeTimeout);

        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(uri.Host, 443, timeout.Token);

            await using var ssl = new SslStream(client.GetStream(), false,
                (_, _, _, errors) => errors == SslPolicyErrors.None);

            await ssl.AuthenticateAsClientAsync(uri.Host);

            if (ssl.RemoteCertificate is not X509Certificate2 certificate)
                return "SSL certificate not provided.";

            if (certificate.NotAfter < DateTime.UtcNow)
                return "SSL certificate expired.";

            return certificate.NotBefore > DateTime.UtcNow ? "SSL certificate is not yet valid." : null;
        }
        catch
        {
            return "SSL certificate validation failed.";
        }
    }
}

public sealed class DomainWildcardDepthValidator(int maxDepth = 3) : OAuthRedirectValidator
{
    public override Task<string?> ValidateAsync(string rawRedirect)
        => Task.FromResult(Validate(rawRedirect));

    private string? Validate(string rawRedirect)
    {
        if (!Uri.TryCreate(rawRedirect, UriKind.Absolute, out var uri))
            return null;

        var labels = uri.Host.Split('.');

        return labels.Length > maxDepth + 1 ? $"Domain depth too large ({labels.Length})." : null;
    }
}
