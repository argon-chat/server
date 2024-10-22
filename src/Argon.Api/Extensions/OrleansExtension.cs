namespace Argon.Api.Extensions;

using Orleans.Configuration;

public static class OrleansExtension
{
    public static WebApplicationBuilder AddOrleans(this WebApplicationBuilder builder)
    {
        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .Configure<ClusterOptions>(cluster =>
                {
                    cluster.ClusterId = "Api";
                    cluster.ServiceId = "Api";
                })
                .AddAdoNetGrainStorage("OrleansStorage", options =>
                {
                    options.Invariant = "Npgsql";
                    options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
                })
                .UseLocalhostClustering();
        });

        return builder;
    }
}