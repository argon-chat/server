namespace Argon.Features;

using Argon.Features.Auth;

public static class HttpContextClientExtensions
{
    extension(HttpContext ctx)
    {
        /// <summary>
        /// What the client says it is: the <c>X-Argon-Client</c> header a first-party client sends,
        /// filled out from the User-Agent for everything it leaves unsaid. Display-only.
        /// </summary>
        public ClientDescriptor GetClientDescriptor()
            => ClientDescriptor.From(
                ctx.Request.Headers.TryGetValue(ClientDescriptor.HeaderName, out var declared) ? declared.ToString() : null,
                ctx.Request.Headers.TryGetValue("User-Agent", out var userAgent) ? userAgent.ToString() : null);
    }
}
