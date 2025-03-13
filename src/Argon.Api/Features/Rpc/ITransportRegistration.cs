namespace Argon.Services;

public interface ITransportRegistration
{
    ITransportRegistration AddService<TInterface, TImpl>()
        where TInterface : class, IArgonService
        where TImpl : class, TInterface;
}