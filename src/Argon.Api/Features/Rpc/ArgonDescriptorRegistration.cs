namespace Argon.Services;

public readonly struct ArgonDescriptorRegistration(IServiceCollection col) : ITransportRegistration
{
    public ITransportRegistration AddService<TInterface, TImpl>() where TInterface : class, IArgonService where TImpl : class, TInterface
    {
        col.AddRpcService<TInterface, TImpl>();
        return this;
    }
}