namespace Argon.Services;

using Features.Jwt;
using Grpc.Core;
using System;
using Transport;

public interface IArgonTransportAuthorizationContext
{
    bool          IsAuthorized { get; }
    TokenUserData User         { get; }
}

public class ArgonTransportDcOffline : Exception;

public class ArgonTransportContext(
    HttpContext RpcContext, IServiceProvider provider, IArgonTransportAuthorizationContext authCtx, IClusterClient? clusterClient) : IDisposable
{
    private static readonly AsyncLocal<ArgonTransportContext> localScope = new();

    public string GetIpAddress()
        => RpcContext.GetIpAddress();

    public string GetRegion()
        => RpcContext.GetRegion();

    public string GetRay()
        => RpcContext.GetRay();

    public string GetClientName()
        => RpcContext.GetClientName();

    public string GetHostName()
        => RpcContext.GetHostName();

    public Guid GetSessionId()
    {
        if (authCtx is WtAuthorizationContext wt)
            return wt.SessionId;

        return RpcContext.GetSessionId();
    }

    public Guid GetMachineId()
        => RpcContext.GetMachineId();

    public static ArgonTransportContext Current
        => localScope.Value ?? throw new InvalidOperationException($"No active transport context");

    public IClusterClient GetClusterClient()
    {
        if (clusterClient is null)
            throw new ArgonTransportDcOffline();
        return clusterClient;
    }

    public static ArgonTransportContext CreateGrpc(ServerCallContext ctx, IServiceProvider provider, IClusterClient? clusterClient)
    {
        if (localScope.Value is not null)
            throw new InvalidAsynchronousStateException($"AsyncLocal of ArgonTransportContext already active");
        return localScope.Value = new ArgonTransportContext(ctx.GetHttpContext(), provider, new GrpcArgonTransportAuthorizationContext(ctx), clusterClient);
    }

    public static ArgonTransportContext CreateWt(HttpContext ctx, TransportClientId clientId, IServiceProvider provider, IClusterClient? clusterClient)
    {
        if (localScope.Value is not null)
            throw new InvalidAsynchronousStateException($"AsyncLocal of ArgonTransportContext already active");
        return localScope.Value = new ArgonTransportContext(ctx, provider, new WtAuthorizationContext(ctx, clientId), clusterClient);
    }


    public bool IsAuthorized => authCtx.IsAuthorized; // RpcContext.UserState.ContainsKey("userToken");

    public TokenUserData User => authCtx.User; //RpcContext.UserState["userToken"] as TokenUserData ?? throw new InvalidOperationException();

    public void Dispose()
        => localScope.Value = null!;
}

public class GrpcArgonTransportAuthorizationContext(ServerCallContext RpcContext) : IArgonTransportAuthorizationContext
{
    public bool          IsAuthorized => RpcContext.UserState.ContainsKey("userToken");
    public TokenUserData User         => RpcContext.UserState["userToken"] as TokenUserData ?? throw new InvalidOperationException();
}

public class WtAuthorizationContext(HttpContext ctx, TransportClientId id) : IArgonTransportAuthorizationContext
{
    public bool          IsAuthorized => true;
    public TokenUserData User         => new(id.userId, Guid.Empty);
    public Guid          SessionId    => id.sessionId;
}