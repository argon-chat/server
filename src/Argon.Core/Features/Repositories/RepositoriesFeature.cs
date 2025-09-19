namespace Argon.Features.Repositories;

public static class RepositoriesFeature
{
    public static IServiceCollection AddEfRepositories(this WebApplicationBuilder builder)
    {
        builder.Services.AddScoped<IServerRepository, ServerRepository>();
        builder.Services.AddScoped<IArchetypeRepository, ArchetypeRepository>();
        return builder.Services;
    }
}