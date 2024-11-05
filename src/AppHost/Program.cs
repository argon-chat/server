using AppHost;
using Projects;

var builder         = DistributedApplication.CreateBuilder(args);
var username        = builder.AddParameter("username", true);
var password        = builder.AddParameter("password", true);
var sfuUrl          = builder.AddParameter("sfu-url", true);
var sfuClientId     = builder.AddParameter("sfu-client-id", true);
var sfuClientSecret = builder.AddParameter("sfu-client-secret", true);
var jwtKey          = builder.AddParameter("jwt-key", true);
var cache           = builder.AddRedis("cache", 6379);
var rmq = builder
   .AddRabbitMQ("rmq", port: 5672, userName: username, password: password)
   .WithDataVolume(isReadOnly: false)
   .WithManagementPlugin();
var db = builder
   .AddPostgres("pg", port: 5432, userName: username, password: password)
   .WithDataVolume();
var clickhouseResource = new ClickhouseBuilderExtension("clickhouse", username, password);
var clickhouse = builder
   .AddResource(clickhouseResource)
   .WithImage("clickhouse/clickhouse-server")
   .WithVolume("clickhouse-data", "/var/lib/clickhouse")
   .WithVolume("logs", "/var/log/clickhouse-server")
   .WithEnvironment("CLICKHOUSE_USER", username)
   .WithEnvironment("CLICKHOUSE_PASSWORD", password)
   .WithEnvironment("CLICKHOUSE_DB", username)
   .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
   .WithHttpEndpoint(8123, 8123) // http endpoint
   .WithEndpoint(9000, 9000);    // native client endpoint
var apiDb = db.AddDatabase("apiDb");
var api = builder
   .AddProject<Argon_Api>("argonapi")
   .WithReference(apiDb, "DefaultConnection")
   .WithReference(cache)
   .WithReference(clickhouse)
   .WithReference(rmq)
   .WithEnvironment("sfu__url", sfuUrl)
   .WithEnvironment("sfu__clientId", sfuClientId)
   .WithEnvironment("sfu__clientSecret", sfuClientSecret)
   .WithEnvironment("Jwt__Issuer", "Argon")
   .WithEnvironment("Jwt__Audience", "Argon")
   .WithEnvironment("Jwt__Key", jwtKey)
   .WithEnvironment("Jwt__Expire", "228")
   .WithExternalHttpEndpoints();
builder.Build().Run();
