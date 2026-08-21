namespace Argon.Features.BotApi;

/// <summary>
/// Marks a bot interface version as stable (frozen).
/// The <paramref name="contractHash"/> is a SHA-256 hash of the API surface
/// (routes, HTTP methods, request/response types, property names/types).
/// At startup the server re-computes the hash and refuses to start if it differs,
/// preventing accidental breaking changes to a published API version.
/// <para>
/// Use <c>dotnet run -- bot-api manifest</c> to compute hashes for all interfaces.
/// </para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class StableContractAttribute(string contractHash) : Attribute
{
    public string ContractHash { get; } = contractHash;
}

/// <summary>
/// Declaratively describes a route on a <see cref="IBotInterface"/> for contract hashing
/// and documentation generation.
/// Applied to the interface class, one per endpoint.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class BotRouteAttribute(string method, string path) : Attribute
{
    public string  Method       { get; } = method;
    public string  Path         { get; } = path;
    public Type?   RequestType  { get; set; }
    public Type?   ResponseType { get; set; }
    public string? Description  { get; set; }
    public string? Permission   { get; set; }
    public bool    IsPrivileged { get; set; }
}

/// <summary>
/// Provides a human-readable description for a <see cref="IBotInterface"/>
/// implementation, used in generated documentation.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class BotDescriptionAttribute(string description) : Attribute
{
    public string Description { get; } = description;
}

/// <summary>
/// Declares a typed error that a route can return.
/// Apply multiple times per class, using <see cref="Route"/> to associate with a specific route path.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public sealed class BotErrorAttribute(string route, int status, string code, string description) : Attribute
{
    /// <summary>Route path this error applies to (e.g. "/Send").</summary>
    public string Route       { get; } = route;
    /// <summary>HTTP status code (e.g. 400, 403, 404).</summary>
    public int    Status      { get; } = status;
    /// <summary>Machine-readable error code in the <c>error</c> field of the response body.</summary>
    public string Code        { get; } = code;
    /// <summary>Human-readable description of when this error occurs.</summary>
    public string Description { get; } = description;
}
