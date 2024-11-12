namespace ActualLab.Rpc.Server;

using DependencyInjection;

public readonly struct RpcWebSocketServerBuilder
{
    public RpcBuilder         Rpc      { get; }
    public IServiceCollection Services => Rpc.Services;

    internal RpcWebSocketServerBuilder(RpcBuilder rpc, Action<RpcWebSocketServerBuilder>? configure)
    {
        Rpc = rpc;
        var services = Services;
        if (services.HasService<RpcWebSocketServer>())
        {
            configure?.Invoke(this);
            return;
        }

        services.AddSingleton(_ => RpcWebSocketServerDefaultDelegates.PeerRefFactory);
        services.AddSingleton(_ => RpcWebSocketServer.Options.Default);
        services.AddSingleton<RpcWebSocketServer>();
        configure?.Invoke(this);
    }

    public RpcWebSocketServerBuilder Configure(Func<IServiceProvider, RpcWebSocketServer.Options> optionsFactory)
    {
        Services.AddSingleton(optionsFactory);
        return this;
    }
}