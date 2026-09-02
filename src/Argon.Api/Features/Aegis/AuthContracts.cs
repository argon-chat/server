namespace Argon.Api.Features.Aegis;

/// <summary>
/// What the sign-in widget sends and is told back.
/// </summary>
/// <remarks>
/// Plain JSON rather than Ion contracts, because the widget is a web page loaded cross-site by
/// applications that are not ours and speaks to this over ordinary <c>fetch</c>. Nothing here
/// carries a token: the widget's job is to establish a session, and the token is issued by the OAuth
/// endpoint afterwards.
/// </remarks>
public record OAuthAuthorizeRequest
{
    public string? Email               { get; init; }
    public string? Phone               { get; init; }
    public string? Username            { get; init; }
    public string? Password            { get; init; }
    public string? OtpCode             { get; init; }
    public string? CaptchaToken        { get; init; }
    public string  ClientId            { get; init; } = "";
    public string? Scope               { get; init; }
    public string? CodeChallenge       { get; init; }
    public string? CodeChallengeMethod { get; init; }
    public string? RedirectUri         { get; init; }
}

/// <summary>
/// The outcome of a credential check, and whatever the user still has to do.
/// </summary>
/// <remarks>
/// The three <c>Requires*</c> flags are steps, not failures: the credential was right and the flow
/// is not finished. A refusal is <see cref="Error"/>, and is deliberately the same shape whichever
/// credential was wrong.
/// </remarks>
public record OAuthAuthorizeResponse
{
    public bool         Success              { get; init; }
    public string?      Error                { get; init; }
    public bool         RequiresOtp          { get; init; }
    public bool         RequiresConsent      { get; init; }
    public bool         RequiresOperatorAuth { get; init; }
    public ConsentInfo? ConsentInfo          { get; init; }
}

/// <summary>What the consent screen shows about the application asking.</summary>
public record ConsentInfo
{
    public string       AppName         { get; init; } = "";
    public string?      AppDescription  { get; init; }
    public string?      AppAvatarFileId { get; init; }
    public string       DeveloperName   { get; init; } = "";
    public string?      WebsiteUrl      { get; init; }
    public bool         IsVerified      { get; init; }
    public List<string> RequestedScopes { get; init; } = [];

    public static ConsentInfo Of(OAuthAppInfo app)
        => new()
        {
            AppName         = app.AppName,
            AppDescription  = app.AppDescription,
            AppAvatarFileId = app.AppAvatarFileId,
            DeveloperName   = app.DeveloperName,
            WebsiteUrl      = app.WebsiteUrl,
            IsVerified      = app.IsVerified,
            RequestedScopes = [.. app.RequestedScopes]
        };
}

public record OAuthCompleteResponse
{
    public bool Success { get; init; }
}

public record SessionCheckResponse
{
    public bool               HasSession               { get; init; }
    public bool               RequiresConsent          { get; init; }
    public bool               RequiresAccountSelection { get; init; }
    public bool               RequiresOperatorAuth     { get; init; }
    public bool               RequiresLogin            { get; init; }
    public bool               AccessDenied             { get; init; }
    public string?            DenialReason             { get; init; }
    public ConsentInfo?       ConsentInfo              { get; init; }
    public List<AccountInfo>? Accounts                 { get; init; }
}

public record AccountInfo
{
    public Guid    UserId       { get; init; }
    public string  Username     { get; init; } = "";
    public string? AvatarFileId { get; init; }

    /// <summary>
    /// Where to fetch this account's avatar, or null when there is nothing to show.
    /// </summary>
    /// <remarks>
    /// The picker used to build this itself out of <see cref="AvatarFileId"/> and a hostname written
    /// into the template — a single region, which is wrong for everyone outside it and names nothing
    /// at all in a self-hosted deployment. A file id says where a file sits in some deployment's
    /// storage; only the server knows how that deployment publishes it, so the server is what says.
    /// </remarks>
    public string? AvatarUrl { get; init; }

    public bool    IsCurrent    { get; init; }
}

public record SelectAccountRequest
{
    public Guid UserId { get; init; }
}

public record GetScenarioRequest
{
    public string? Email    { get; init; }
    public string? Phone    { get; init; }
    public string? Username { get; init; }
}

public record BeginPasskeyRequest
{
    public string? Email { get; init; }
}

public record CompletePasskeyRequest
{
    public string  Nonce                 { get; init; } = "";
    public string  AssertionResponseJson { get; init; } = "";
    public string? ClientId              { get; init; }
    public string? Scope                 { get; init; }
}

public record ConfirmPasskeyOtpRequest
{
    public string  PasskeyNonce { get; init; } = "";
    public string  OtpCode      { get; init; } = "";
    public string? ClientId     { get; init; }
    public string? Scope        { get; init; }
}

public record PasskeyBeginResponse
{
    public bool    Success     { get; init; }
    public string? OptionsJson { get; init; }
    public string? Error       { get; init; }
}

public record PasskeyCompleteResponse
{
    public bool         Success         { get; init; }
    public bool         RequiresOtp     { get; init; }
    public bool         RequiresConsent { get; init; }
    public string?      PasskeyNonce    { get; init; }
    public string?      Error           { get; init; }
    public ConsentInfo? ConsentInfo     { get; init; }
}
