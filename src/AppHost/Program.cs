var builder = DistributedApplication.CreateBuilder(args);

var username = builder.AddParameter("username", true);
var password = builder.AddParameter("password", true);

var cache = builder.AddRedis("cache", port: 6379);
var rmq = builder.AddRabbitMQ("rmq", port: 5672, userName: username, password: password)
    .WithDataVolume(isReadOnly: false)
    .WithManagementPlugin();
var db = builder.AddPostgres("pg", port: 5432, userName: username, password: password)
    .WithDataVolume();

var apiDb = db.AddDatabase("apiDb");

var api = builder.AddProject<Projects.Argon_Api>("argon-api")
    .WithReference(apiDb, "DefaultConnection")
    .WithReference(cache)
    .WithReference(rmq)
    .WithExternalHttpEndpoints();

builder.Build().Run();