namespace Argon.Api.Features.Aegis;

using System.Collections.Immutable;
using System.Text.Json;
using Argon.Features.Aegis;
using Argon.Features.Storage;
using OpenIddict.Abstractions;
using OpenIddict.Server;
using static OpenIddict.Server.OpenIddictServerEvents;

/// <summary>
/// Decides whether an authorization request may proceed at all.
/// </summary>
/// <remarks>
/// OpenIddict runs in degraded mode here — it keeps no client store of its own, so nothing has
/// checked that the client exists, that the redirect it named is one of its own, or that the scopes
/// it asked for are ones it was granted. That is what this does, and it runs before a code is minted
/// rather than after: a redirect_uri accepted here is where the code gets sent.
/// </remarks>
public sealed class ValidateAuthorizationHandler(IAegisDirectory directory)
    : IOpenIddictServerHandler<ValidateAuthorizationRequestContext>
{
    /// <summary>
    /// Standard OIDC scopes every client may ask for regardless of its registration — refusing
    /// <c>openid</c> would refuse OpenID Connect itself.
    /// </summary>
    private static readonly HashSet<string> AlwaysAllowed = new(StringComparer.OrdinalIgnoreCase)
    {
        OpenIddictConstants.Scopes.OpenId,
        OpenIddictConstants.Scopes.Profile
    };

    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateAuthorizationRequestContext>()
           .UseScopedHandler<ValidateAuthorizationHandler>()
           .SetOrder(OpenIddictServerHandlers.Exchange.ValidateGrantType.Descriptor.Order + 10_000)
           .Build();

    public async ValueTask HandleAsync(ValidateAuthorizationRequestContext context)
    {
        var clientId    = context.ClientId;
        var redirectUri = context.RedirectUri;

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirectUri))
        {
            context.Reject(OpenIddictConstants.Errors.InvalidRequest, "Missing client_id or redirect_uri.");
            return;
        }

        var credentials = await directory.GetAppCredentialsAsync(clientId);

        if (credentials is null)
        {
            context.Reject(OpenIddictConstants.Errors.InvalidClient, "Unknown client_id.");
            return;
        }

        if (!credentials.allowedRedirects.Contains(redirectUri, StringComparer.OrdinalIgnoreCase))
        {
            context.Reject(OpenIddictConstants.Errors.InvalidClient,
                "The specified redirect_uri is not allowed for this client.");
            return;
        }

        var disallowed = context.Request.GetScopes()
           .Except(credentials.scopes)
           .Where(scope => !AlwaysAllowed.Contains(scope))
           .ToList();

        if (disallowed.Count > 0)
            context.Reject(OpenIddictConstants.Errors.InvalidScope,
                $"Client is not allowed to request scopes: {string.Join(", ", disallowed)}, " +
                $"defined allowed: {string.Join(',', credentials.scopes)}");
    }
}

/// <summary>
/// Authenticates the client at the token endpoint.
/// </summary>
/// <remarks>
/// Only the client-credentials grant is checked here, and deliberately so: it is the one grant where
/// the client secret <i>is</i> the whole credential. The code and refresh grants carry a token this
/// server minted and encrypted, and OpenIddict has already established that it is genuine by the
/// time this runs.
/// </remarks>
public sealed class ValidateTokenHandler(IAegisDirectory directory)
    : IOpenIddictServerHandler<ValidateTokenRequestContext>
{
    public static OpenIddictServerHandlerDescriptor Descriptor { get; }
        = OpenIddictServerHandlerDescriptor.CreateBuilder<ValidateTokenRequestContext>()
           .UseScopedHandler<ValidateTokenHandler>()
           .SetOrder(OpenIddictServerHandlers.Exchange.ValidateGrantType.Descriptor.Order + 10_000)
           .Build();

    public async ValueTask HandleAsync(ValidateTokenRequestContext context)
    {
        var clientId = context.ClientId;

        if (string.IsNullOrWhiteSpace(clientId))
        {
            context.Reject(OpenIddictConstants.Errors.InvalidClient, "Missing client_id.");
            return;
        }

        var credentials = await directory.GetAppCredentialsAsync(clientId);

        if (credentials is null)
        {
            context.Reject(OpenIddictConstants.Errors.InvalidClient, "Unknown client_id.");
            return;
        }

        if (context.Request.IsClientCredentialsGrantType() &&
            !ClientSecret.Matches(credentials.ClientSecret, context.Request.ClientSecret))
            context.Reject(OpenIddictConstants.Errors.InvalidClient, "Invalid client_secret.");
    }
}

/// <summary>
/// Answers <c>/connect/userinfo</c>, one claim per granted scope.
/// </summary>
/// <remarks>
/// The shape of this is the point: every block is guarded by the scope that entitles the caller to
/// it, so an application that asked for <c>user.read</c> and nothing else gets a username and no
/// mailbox. The user's own record is read fresh here rather than replayed from the token, which is
/// what makes this the endpoint an application asks when it wants the current answer.
/// <para>
/// Operator claims are the exception and come from the access token instead. They record a
/// hardware-key step-up that happened during a particular authorization; re-reading them from the
/// database would report that someone <i>is</i> an operator, which is not the same as their having
/// proved it for this token.
/// </para>
/// </remarks>
public sealed class UserInfoHandler(IClusterClient cluster, IOptions<AegisOptions> options)
    : IOpenIddictServerHandler<HandleUserInfoRequestContext>
{
    public async ValueTask HandleAsync(HandleUserInfoRequestContext context)
    {
        if (!Guid.TryParse(context.Subject, out var userId))
        {
            context.Reject("bad subject", "subject is not valid value");
            return;
        }

        var scopes  = context.AccessTokenPrincipal.GetScopes();
        var user    = cluster.GetGrain<IUserGrain>(userId);
        var me      = await user.GetMe();
        var profile = await user.GetMyProfile();

        var isStaff = profile.badges.Values.Contains("staff");

        if (scopes.Contains(ArgonScopes.Email))
        {
            context.Email         = me.Email.ToLowerInvariant();
            context.EmailVerified = true;
        }

        if (scopes.Contains(ArgonScopes.UserRead))
        {
            context.PreferredUsername = me.Username.ToLowerInvariant();
            context.Claims.Add("displayName", new OpenIddictParameter(me.DisplayName));

            // An address rather than an identifier, because an identifier is only useful to somebody
            // who already knows how this deployment stores files — and an application integrating
            // over OIDC does not, and should not have to. Absent rather than empty when there is no
            // avatar: a consumer checking for the field is right, and one that would have rendered an
            // empty string is not given the chance.
            if (options.Value.AvatarUrlFor(me.AvatarFileId) is { } avatarUrl)
                context.Claims.Add("avatarUrl", new OpenIddictParameter(avatarUrl));
        }

        if (scopes.Contains(ArgonScopes.Role))
            context.Claims.Add("badges", new OpenIddictParameter([.. profile.badges.Values]));

        if (scopes.Contains(ArgonScopes.InternalRead))
        {
            context.Claims.Add(ArgonScopes.InternalRead, new OpenIddictParameter(isStaff));

            if (me.Email.EndsWith("argon.gl"))
                context.Claims.Add("tenant", new OpenIddictParameter("argon.staff"));
        }

        if (scopes.Contains(ArgonScopes.InternalWrite))
            context.Claims.Add(ArgonScopes.InternalWrite, new OpenIddictParameter(isStaff));

        if (scopes.Contains(ArgonScopes.InfrastructureRead))
            context.Claims.Add(ArgonScopes.InfrastructureRead, new OpenIddictParameter(isStaff));

        if (scopes.Contains(ArgonScopes.InfrastructureWrite))
            context.Claims.Add(ArgonScopes.InfrastructureWrite, new OpenIddictParameter(isStaff));

        context.Claims.Add("isBanned", new OpenIddictParameter(me.LockDownIsAppealable));

        AddOperatorClaims(context, scopes);
    }

    private static void AddOperatorClaims(HandleUserInfoRequestContext context, ImmutableArray<string> scopes)
    {
        var principal  = context.AccessTokenPrincipal;
        var operatorId = principal?.FindFirst(AegisClaims.OperatorId)?.Value;

        if (string.IsNullOrEmpty(operatorId))
            return;

        var operatorEmail = principal?.FindFirst(AegisClaims.OperatorEmail)?.Value;

        context.Claims.Add(AegisClaims.OperatorId, new OpenIddictParameter(operatorId));
        context.Claims.Add(AegisClaims.OperatorVerified, new OpenIddictParameter(true));

        if (!string.IsNullOrEmpty(operatorEmail))
            context.Claims.Add(AegisClaims.OperatorEmail, new OpenIddictParameter(operatorEmail));

        if (scopes.Contains(ArgonScopes.Profile) &&
            principal?.FindFirst(OpenIddictConstants.Claims.Name)?.Value is { Length: > 0 } name)
            context.Claims[OpenIddictConstants.Claims.Name] = new OpenIddictParameter(name);

        // Federated identity: for an operator the operator mailbox is the identity, not the personal
        // account the step-up was performed from.
        if (scopes.Contains(ArgonScopes.Email) && !string.IsNullOrEmpty(operatorEmail))
        {
            context.Email         = operatorEmail.ToLowerInvariant();
            context.EmailVerified = true;
        }

        if (scopes.Contains(ArgonScopes.Role))
        {
            var roles = principal?.FindAll(AegisClaims.Roles).Select(c => c.Value).ToArray() ?? [];

            if (roles.Length > 0)
                context.Claims[AegisClaims.Roles] =
                    new OpenIddictParameter(JsonSerializer.SerializeToElement(roles));
        }

        if (principal?.FindFirst(AegisClaims.AuthenticationMethod)?.Value is { Length: > 0 } amr)
            context.Claims[AegisClaims.AuthenticationMethod] =
                new OpenIddictParameter(JsonSerializer.SerializeToElement(new[] { amr }));
    }
}
