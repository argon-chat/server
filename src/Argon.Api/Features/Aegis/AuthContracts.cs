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
/// What the widget sends to create an account.
/// </summary>
/// <remarks>
/// The same fields the installed client collects — see <c>NewUserCredentialsInput</c>, which this is
/// turned into — plus the OAuth pair, because a registration started from the widget is the first
/// leg of an authorization request and has to end on the same consent screen a sign-in does.
/// <para>
/// <c>BirthDate</c> is a string rather than a date: it arrives from an <c>&lt;input type="date"&gt;</c>
/// as <c>yyyy-MM-dd</c>, and a value the browser could not produce is a field error to show next to
/// the field rather than a 400 with a model-binding message nobody wrote.
/// </para>
/// </remarks>
public record OAuthRegisterRequest
{
    public string  Email               { get; init; } = "";
    public string  Username            { get; init; } = "";
    public string  Password            { get; init; } = "";
    public string  DisplayName         { get; init; } = "";
    public string  BirthDate           { get; init; } = "";
    public bool    AgreeTos            { get; init; }
    public bool    AgreeOptionalEmails { get; init; }
    public string? CaptchaToken        { get; init; }
    public string? TosVersion          { get; init; }
    public string? PrivacyVersion      { get; init; }
    public string  ClientId            { get; init; } = "";
    public string? Scope               { get; init; }
}

/// <summary>
/// The outcome of a registration, in the same shape a sign-in answers with.
/// </summary>
/// <remarks>
/// Deliberately the same flags: a registration that succeeds has established exactly the session a
/// sign-in would have, so the widget carries on into consent through the code it already has rather
/// than through a second path that would drift.
/// <para>
/// <see cref="Field"/> is what registration adds. Its refusals are about one input — this username
/// is taken, that birth date is too recent — and a form that can point at the field is the whole
/// difference between a usable sign-up and one that says "registration failed".
/// </para>
/// </remarks>
public record OAuthRegisterResponse
{
    public bool         Success         { get; init; }
    public string?      Error           { get; init; }
    public string?      Field           { get; init; }
    public string?      Message         { get; init; }
    public bool         RequiresConsent { get; init; }
    public ConsentInfo? ConsentInfo     { get; init; }
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
