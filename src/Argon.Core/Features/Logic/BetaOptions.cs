namespace Argon.Features.Logic;

public class BetaLimitationOptions
{
    public List<Guid> AllowedCreationSpaceUsers { get; init; } = new();
}

public static class BetaExtensionsOptions
{
    public static void MapBetaOptions(this WebApplicationBuilder app)
    {
        app.Services.Configure<BetaLimitationOptions>(app.Configuration.GetSection("Beta"));
    }
}