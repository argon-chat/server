namespace Argon.Services;

public static class RpcServiceCollectionExtensions
{
    public static void MapArgonTransport(this WebApplication app)
    {
        app.MapGrpcService<ArgonTransport>().EnableGrpcWeb();
        app.UseGrpcWeb(new GrpcWebOptions
        {
            DefaultEnabled = true
        });
    }

    public static void AddArgonTransport(this WebApplicationBuilder builder, Action<ITransportRegistration> onRegistration)
    {
        var col = builder.Services;
        col.Configure<ArgonTransportOptions>(_ => { });
        col.AddSingleton<ArgonDescriptorStorage>();
        var reg = new ArgonDescriptorRegistration(col);
        onRegistration(reg);
        col.AddGrpc(x => { x.Interceptors.Add<AuthInterceptor>(); });
    }


    public static IServiceCollection AddRpcService<TInterface, TImplementation>(this IServiceCollection services)
        where TInterface : class, IArgonService
        where TImplementation : class, TInterface
    {
        services.AddSingleton<TInterface, TImplementation>();

        services.Configure<ArgonTransportOptions>(options => { options.Services.Add(typeof(TInterface), typeof(TImplementation)); });

        return services;
    }
}

public interface ITransportRegistration
{
    ITransportRegistration AddService<TInterface, TImpl>()
        where TInterface : class, IArgonService
        where TImpl : class, TInterface;
}

public readonly struct ArgonDescriptorRegistration(IServiceCollection col) : ITransportRegistration
{
    public ITransportRegistration AddService<TInterface, TImpl>() where TInterface : class, IArgonService where TImpl : class, TInterface
    {
        col.AddRpcService<TInterface, TImpl>();
        return this;
    }
}