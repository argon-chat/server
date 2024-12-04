namespace Argon.Services;

public record ArgonTransportOptions
{
    public Dictionary<Type, Type> Services { get; } = new();
}

public class ArgonDescriptorStorage
{
    private readonly Dictionary<string, IArgonService> _services = new();

    public ArgonDescriptorStorage(IServiceProvider serviceProvider, IOptions<ArgonTransportOptions> options, ILogger<ArgonDescriptorStorage> logger)
    {
        foreach (var (interfaceType, implementationType) in options.Value.Services)
        {
            if (serviceProvider.GetService(interfaceType) is not IArgonService service) continue;
            _services.Add(interfaceType.Name, service);
            logger.LogTrace("[RpcServiceStorage] Registered: {interfaceType.Name}", interfaceType.Name);
        }
    }

    public IArgonService GetService(string serviceName)
    {
        if (_services.TryGetValue(serviceName, out var service))
            return service;

        throw new InvalidOperationException($"Service '{serviceName}' not found.");
    }
}