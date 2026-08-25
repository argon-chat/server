namespace Argon.Features.Aegis;

/// <summary>
/// Every scope this provider knows how to issue.
/// </summary>
public static class ArgonScopes
{
    public const string Identity            = "identity";
    public const string Profile             = "profile";
    public const string UserRead            = "user.read";
    public const string UserEMail           = "user.email";
    public const string Email               = "email";
    public const string OfflineAccess       = "offline_access";
    public const string NestedDomain        = "nd";
    public const string Role                = "role";
    public const string InternalRead        = "internal.read";
    public const string InternalWrite       = "internal.write";
    public const string InfrastructureRead  = "infrastructure.read";
    public const string InfrastructureWrite = "infrastructure.write";

    public const string CalendarRead = "calendar.readonly";
    public const string Calendar     = "calendar";
    public const string EmailSend    = "email:send";

    /// <summary>The default for <c>OpenId:Scopes</c>, so the list is written down once.</summary>
    public static readonly string[] All =
    [
        Identity, Profile, UserRead, UserEMail, Email, OfflineAccess, NestedDomain, Role,
        InternalRead, InternalWrite, InfrastructureRead, InfrastructureWrite,
        CalendarRead, Calendar, EmailSend
    ];
}
