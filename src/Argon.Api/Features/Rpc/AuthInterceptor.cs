namespace Argon.Services;

using Features.Jwt;
using Grpc.Core;
using Grpc.Core.Interceptors;

public class AuthInterceptor(TokenAuthorization tokenAuthorization) : Interceptor
{
    public async override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        var authorizationHeader = context.RequestHeaders
           .FirstOrDefault(h => h.Key.Equals("authorize", StringComparison.InvariantCultureIgnoreCase));
        if (authorizationHeader is null)
            return await continuation(request, context);

        var authResult = await tokenAuthorization.AuthorizeByToken(authorizationHeader.Value);

        if (!authResult.IsSuccess)
            return await continuation(request, context);

        context.UserState.Add("userToken", authResult.Value);

        return await continuation(request, context);
    }
}