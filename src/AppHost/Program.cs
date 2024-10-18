using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var username = builder.AddParameter("username", true);
var password = builder.AddParameter("password", true);
var sfuUrl = builder.AddParameter("sfu-url", true);
var sfuClientId = builder.AddParameter("sfu-client-id", true);
var sfuClientSecret = builder.AddParameter("sfu-client-secret", true);

var cache = builder.AddRedis("cache", 6379);
var rmq = builder.AddRabbitMQ("rmq", port: 5672, userName: username, password: password)
    .WithDataVolume(isReadOnly: false)
    .WithManagementPlugin();
var db = builder.AddPostgres("pg", port: 5432, userName: username, password: password)
    .WithDataVolume();

var apiDb = db.AddDatabase("apiDb");

var api = builder.AddProject<Argon_Api>("argon-api")
    .WithReference(apiDb, "DefaultConnection")
    .WithReference(cache)
    .WithReference(rmq)
    .WithEnvironment("sfu__url", sfuUrl)
    .WithEnvironment("sfu__clientId", sfuClientId)
    .WithEnvironment("sfu__clientSecret", sfuClientSecret)
    .WithExternalHttpEndpoints();

builder.Build().Run();