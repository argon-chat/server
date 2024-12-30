namespace Argon.Services;

using Features.Jwt;
using Genbox.SimpleS3.Core.Abstracts.Request;
using Grpc.Core;
using Grpc.Core.Interceptors;
using k8s.KubeConfigModels;
using Orleans.Runtime;

public class AuthInterceptor(TokenAuthorization tokenAuthorization, ILogger<AuthInterceptor> logger) : Interceptor
{
    public async override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(TRequest request, ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        await DoAuthorize(context);
        return await continuation(request, context);
    }


    private async Task<bool> DoAuthorize(ServerCallContext context)
    {
        var authorizationHeader = context.RequestHeaders
           .FirstOrDefault(h => h.Key.Equals("authorize", StringComparison.InvariantCultureIgnoreCase));
        if (authorizationHeader is null)
        {
            logger.LogWarning($"No authorization token is defined, skip...");
            return false;
        }

        var authResult = await tokenAuthorization.AuthorizeByToken(authorizationHeader.Value);

        if (!authResult.IsSuccess)
        {
            logger.LogError($"Failed authorization, error: {authResult.Error}");
            return false;
        }

        context.UserState.Add("userToken", authResult.Value);

        return true;
    }

    public async override Task ServerStreamingServerHandler<TRequest, TResponse>(TRequest request, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await DoAuthorize(context);
        await continuation(request, responseStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream, IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context, DuplexStreamingServerMethod<TRequest, TResponse> continuation)
        => base.DuplexStreamingServerHandler(requestStream, responseStream, context, continuation);

    public async override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await DoAuthorize(context);
        return await continuation(requestStream, context);
    }
}