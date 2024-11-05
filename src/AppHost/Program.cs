using AppHost;
using Projects;

var builder = DistributedApplication.CreateBuilder(args: args);

var username        = builder.AddParameter(name: "username",          secret: true);
var password        = builder.AddParameter(name: "password",          secret: true);
var sfuUrl          = builder.AddParameter(name: "sfu-url",           secret: true);
var sfuClientId     = builder.AddParameter(name: "sfu-client-id",     secret: true);
var sfuClientSecret = builder.AddParameter(name: "sfu-client-secret", secret: true);
var jwtKey          = builder.AddParameter(name: "jwt-key",           secret: true);

var cache = builder.AddRedis(name: "cache", port: 6379);
var rmq = builder.AddRabbitMQ(name: "rmq", port: 5672, userName: username, password: password)
                 .WithDataVolume(isReadOnly: false)
                 .WithManagementPlugin();
var db = builder.AddPostgres(name: "pg", port: 5432, userName: username, password: password)
                .WithDataVolume();

var clickhouseResource = new ClickhouseBuilderExtension(name: "clickhouse", userName: username, password: password);
var clickhouse = builder.AddResource(resource: clickhouseResource)
                        .WithImage(image: "clickhouse/clickhouse-server")
                        .WithVolume(name: "clickhouse-data", target: "/var/lib/clickhouse")
                        .WithVolume(name: "logs",            target: "/var/log/clickhouse-server")
                        .WithEnvironment(name: "CLICKHOUSE_USER",                      parameter: username)
                        .WithEnvironment(name: "CLICKHOUSE_PASSWORD",                  parameter: password)
                        .WithEnvironment(name: "CLICKHOUSE_DB",                        parameter: username)
                        .WithEnvironment(name: "CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", value: "1")
                        .WithHttpEndpoint(port: 8123, targetPort: 8123) // http endpoint
                        .WithEndpoint(port: 9000, targetPort: 9000);    // native client endpoint

var apiDb = db.AddDatabase(name: "apiDb");

var api = builder.AddProject<Argon_Api>(name: "argonapi")
                 .WithReference(source: apiDb, connectionName: "DefaultConnection")
                 .WithReference(source: cache)
                 .WithReference(source: clickhouse)
                 .WithReference(source: rmq)
                 .WithEnvironment(name: "sfu__url",          parameter: sfuUrl)
                 .WithEnvironment(name: "sfu__clientId",     parameter: sfuClientId)
                 .WithEnvironment(name: "sfu__clientSecret", parameter: sfuClientSecret)
                 .WithEnvironment(name: "Jwt__Issuer",       value: "Argon")
                 .WithEnvironment(name: "Jwt__Audience",     value: "Argon")
                 .WithEnvironment(name: "Jwt__Key",          parameter: jwtKey)
                 .WithEnvironment(name: "Jwt__Expire",       value: "228")
                 .WithExternalHttpEndpoints();

builder.Build().Run();