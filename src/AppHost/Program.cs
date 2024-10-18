using Projects;

var builder = DistributedApplication.CreateBuilder(args);

var username = builder.AddParameter("username", true);
var password = builder.AddParameter("password", true);

var cache = builder.AddRedis("cache", 6379);
var rmq = builder.AddRabbitMQ("rmq", port: 5672, userName: username, password: password)
    .WithDataVolume(isReadOnly: false)
    .WithManagementPlugin();
var db = builder.AddPostgres("pg", port: 5432, userName: username, password: password)
    .WithDataVolume();

var apiDb = db.AddDatabase("apiDb");

// var api = builder.AddProject<Argon_Api>("argon-api")
//     .WithReference(apiDb, "DefaultConnection")
//     .WithReference(cache)
//     .WithReference(rmq)
//     .WithEndpoint(11111, 11111, "tcp", "siloPort", isProxied: false)
//     .WithEndpoint(30000, 30000, "tcp", "grainPort", isProxied: false)
//     .WithExternalHttpEndpoints();

builder.Build().Run();