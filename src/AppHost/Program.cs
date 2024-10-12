var builder = DistributedApplication.CreateBuilder(args);

var cache = builder.AddRedis("cache");
var username = builder.AddParameter("username", true);
var password = builder.AddParameter("password", true);
var db = builder.AddPostgres("pg", port: 5432, userName: username, password: password)
        .WithDataVolume();

var apiDb = db.AddDatabase("apiDb");

var api = builder.AddProject<Projects.Argon_Api>("Argon.Api")
        .WithReference(apiDb, "DefaultConnection")
        .WithReference(cache)
        .WithExternalHttpEndpoints();

builder.Build().Run();