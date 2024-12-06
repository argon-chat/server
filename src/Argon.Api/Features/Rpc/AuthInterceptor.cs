namespace Argon.Services;

using Features.Jwt;
using Grpc.Core;
using Grpc.Core.Interceptors;

public class AuthInterceptor(TokenAuthorization tokenAuthorization, ILogger<AuthInterceptor> logger) : Interceptor
{
    public async override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var authorizationHeader = context.RequestHeaders
           .FirstOrDefault(h => h.Key.Equals("authorize", StringComparison.InvariantCultureIgnoreCase));
        if (authorizationHeader is null)
        {
            logger.LogWarning($"No authorization token is defined, skip...");
            return await continuation(request, context);
        }

        var authResult = await tokenAuthorization.AuthorizeByToken(authorizationHeader.Value);

        if (!authResult.IsSuccess)
        {
            logger.LogError($"Failed authorization, error: {authResult.Error}");
            return await continuation(request, context);
        }

        context.UserState.Add("userToken", authResult.Value);

        return await continuation(request, context);
    }
}