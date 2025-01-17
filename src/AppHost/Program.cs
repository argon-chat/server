using AppHost;
using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var username        = builder.AddParameter("username", true);
var password        = builder.AddParameter("password", true);
var sfuUrl          = builder.AddParameter("sfu-url", true);
var sfuClientId     = builder.AddParameter("sfu-client-id", true);
var sfuClientSecret = builder.AddParameter("sfu-client-secret", true);
var jwtKey          = builder.AddParameter("jwt-key", true);
var smtpHost        = builder.AddParameter("smtp-host", true);
var smtpPort        = builder.AddParameter("smtp-port", true);
var smtpUser        = builder.AddParameter("smtp-user", true);
var smtpPassword    = builder.AddParameter("smtp-password", true);

var cache = builder.AddRedis("cache", 6379).WithImage("eqalpha/keydb").WithDataVolume().WithLifetime(ContainerLifetime.Persistent);
var nats = builder.AddNats("nats", 4222).WithImage("nats").WithHttpEndpoint(8222, 8222).WithDataVolume().WithJetStream()
   .WithLifetime(ContainerLifetime.Persistent);
var db = builder.AddPostgres("pg", port: 5432, userName: username, password: password).WithDataVolume().WithLifetime(ContainerLifetime.Persistent);

var clickhouseResource = new ClickhouseBuilderExtension("clickhouse", username, password);
var clickhouse = builder.AddResource(clickhouseResource).WithImage("clickhouse/clickhouse-server").WithImageTag("23")
   .WithVolume("clickhouse-data", "/var/lib/clickhouse").WithVolume("logs", "/var/log/clickhouse-server")
   .WithEnvironment("CLICKHOUSE_USER", username).WithEnvironment("CLICKHOUSE_PASSWORD", password).WithEnvironment("CLICKHOUSE_DB", username)
   .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1").WithHttpEndpoint(8123, 8123) // http endpoint
   .WithEndpoint(9000, 9000).WithLifetime(ContainerLifetime.Persistent);                      // native client endpoint

builder.AddContainer("smtpdev", "rnwood/smtp4dev").WithEndpoint(3080, 80, "http").WithEndpoint(2525, 25, "tcp");

var apiDb = db.AddDatabase("apiDb");

var api = builder.AddProject<Argon_Api>("argon-api").WithReference(apiDb, "DefaultConnection").WithReference(cache).WithReference(clickhouse)
   .WithReference(nats).WithEnvironment("sfu__url", sfuUrl).WithEnvironment("sfu__clientId", sfuClientId)
   .WithEnvironment("sfu__clientSecret", sfuClientSecret).WithEnvironment("Jwt__Issuer", "Argon").WithEnvironment("Jwt__Audience", "Argon")
   .WithEnvironment("Smtp__Host", smtpHost).WithEnvironment("Smtp__Port", smtpPort).WithEnvironment("Smtp__User", smtpUser)
   .WithEnvironment("Smtp__Password", smtpPassword).WithEnvironment("Jwt__Key", jwtKey).WithEnvironment("Jwt__Expire", "228")
   .WithExternalHttpEndpoints().WaitFor(cache).WaitFor(apiDb).WaitFor(clickhouse).WaitFor(nats);

builder.AddProject<Argon_Entry>("argon-entry").WithReference(api).WaitFor(api);

builder.Build().Run();