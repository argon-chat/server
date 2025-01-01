namespace Argon.Services;

using Features.Jwt;
using Grpc.Core;
using Transport;

public class ArgonTransportContext(ServerCallContext RpcContext, RpcRequest Request, IServiceProvider provider) : IDisposable
{
    private static readonly AsyncLocal<ArgonTransportContext> localScope = new();

    public string GetIpAddress()
        => RpcContext.GetHttpContext().GetIpAddress();

    public string GetRegion()
        => RpcContext.GetHttpContext().GetRegion();

    public string GetRay()
        => RpcContext.GetHttpContext().GetRay();

    public string GetClientName()
        => RpcContext.GetHttpContext().GetClientName();

    public string GetHostName()
        => RpcContext.GetHttpContext().GetHostName();

    public static ArgonTransportContext Current
        => localScope.Value ?? throw new InvalidOperationException($"No active transport context");

    public static ArgonTransportContext Create(ServerCallContext ctx, RpcRequest request, IServiceProvider provider)
    {
        if (localScope.Value is not null)
            throw new InvalidAsynchronousStateException($"AsyncLocal of ArgonTransportContext already active");
        return localScope.Value = new ArgonTransportContext(ctx, request, provider);
    }

    public void SubscribeToDisconnect(Func<ValueTask> onDisconnect)
    {
        var r = new RegistrationScope();
        r.refDisposable = RpcContext.CancellationToken.Register((x) =>
        {
            if (x is RegistrationScope scope)
                scope.Dispose();
            Task.Run(async () => await onDisconnect());
        }, r);
    }

    private record RegistrationScope : IDisposable
    {
        public IDisposable? refDisposable;
        public void Dispose()
            => refDisposable?.Dispose();
    }

    public bool IsAuthorized => RpcContext.UserState.ContainsKey("userToken");

    public TokenUserData User => RpcContext.UserState["userToken"] as TokenUserData ?? throw new InvalidOperationException();

    public void Dispose()
        => localScope.Value = null!;

    
}


