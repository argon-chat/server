namespace Argon.Features.EF;

using Argon.Cassandra.Core;
using Cassandra.Configuration;
using Env;
using global::Cassandra;

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
        builder.Services.AddSingleton<CassandraConfiguration>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<CassandraOptions>>();
            return new CassandraConfiguration
            {
                ContactPoints      = opt.Value.ContactPoints,
                Port               = 9042,
                Keyspace           = opt.Value.KeySpace,
                AutoCreateKeyspace = true,
                ReplicationFactor  = 1,
                ConnectionTimeout  = 30000,
                QueryTimeout       = 30000
            };
        });

        builder.Services.AddScoped<ArgonCassandraDbContext>();
        builder.Services.AddSingleton<ICassandraDbContextFactory<ArgonCassandraDbContext>, CassandraDbContextFactory<ArgonCassandraDbContext>>();
    }
}

public record CassandraOptions
{
    public string[] ContactPoints { get; set; } = [];
    public string   KeySpace      { get; set; }
}