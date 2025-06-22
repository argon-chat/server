namespace Argon.Features.EF;

using Cassandra;
using Env;

public static class CassandraFeature
{
    public static void AddCassandraPooledContext(this WebApplicationBuilder builder)
    {
        var featureOptions = builder.GetFeatureOptions();

        if (!featureOptions.UseCassandra) return;

        builder.Services.Configure<CassandraOptions>(builder.Configuration.GetSection("Cassandra"));

        builder.Services.AddSingleton<ICluster>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<CassandraOptions>>();
            return Cluster.Builder()
               .WithApplicationName("argon")
               .AddContactPoints(opt.Value.ContactPoints)
               .Build();
        });

        builder.Services.AddSingleton<ISession>(sp =>
        {
            var opt     = sp.GetRequiredService<IOptions<CassandraOptions>>();
            var cluster = sp.GetRequiredService<ICluster>();
            return cluster.Connect(opt.Value.KeySpace);
        });

        builder.Services.AddSingleton<CassandraMigrationController>();
    }
}


public record CassandraOptions
{
    public string[] ContactPoints { get; set; } = [];
    public string   KeySpace      { get; set; }
}