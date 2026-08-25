namespace Argon.Api.Features.Aegis;

using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;

/// <summary>
/// The token endpoint: three grants, and what each of them has to say before a token comes out.
/// </summary>
/// <remarks>
/// Two of them say nothing here at all. An authorization code and a refresh token were both minted
/// and encrypted by this server, so by the time this runs OpenIddict has already decrypted one,
/// checked it has not been used or expired, and rebuilt the principal that was stored in it —
/// re-issuing is then just handing that principal back.
/// <para>
/// Client credentials is the one that builds a principal from nothing, because there is no user in
/// it: the application is the subject. Its secret was checked by
/// <see cref="ValidateTokenHandler"/> before this point, which is why nothing is checked here.
/// </para>
/// </remarks>
public static class TokenEndpoint
{
    public static void MapAegisTokenEndpoint(this WebApplication app, string path)
        => app.MapPost(path, async (HttpContext http) =>
        {
            var request = http.GetOpenIddictServerRequest()
                       ?? throw new InvalidOperationException("Not an OpenID Connect request.");

            if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
            {
                var result = await http.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

                if (!result.Succeeded || result.Principal is null)
                    return Results.BadRequest(new
                    {
                        error             = OpenIddictConstants.Errors.InvalidGrant,
                        error_description = request.IsRefreshTokenGrantType()
                            ? "Invalid refresh token."
                            : "Invalid authorization code."
                    });

                return Results.SignIn(result.Principal, properties: null,
                    authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            if (request.IsClientCredentialsGrantType())
            {
                var identity = new ClaimsIdentity(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

                // The application is the subject: there is nobody else in this grant.
                identity.AddClaim(new Claim(OpenIddictConstants.Claims.Subject, request.ClientId ?? ""));
                identity.AddClaim(new Claim(OpenIddictConstants.Claims.ClientId, request.ClientId ?? ""));
                identity.SetScopes(request.GetScopes());

                return Results.SignIn(new ClaimsPrincipal(identity), properties: null,
                    authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            return Results.BadRequest(new
            {
                error             = OpenIddictConstants.Errors.UnsupportedGrantType,
                error_description = "The specified grant type is not supported."
            });
        });
}
