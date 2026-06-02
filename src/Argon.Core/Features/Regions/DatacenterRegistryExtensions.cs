    namespace Argon.Features.Regions;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

public static class DatacenterRegistryExtensions
{
    public static IHostApplicationBuilder AddDatacenterRegistry(this IHostApplicationBuilder builder)
    {
        builder.Services.AddHostedService<DatacenterRegistryService>();
        builder.Services.AddSingleton<RegionOperations>();
        return builder;
    }
}
