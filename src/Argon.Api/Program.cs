using System.Data.Common;
using System.Text.Json.Serialization;
using Argon.Api.Entities;
using Argon.Api.Migrations;
using Argon.Sfu;
using Newtonsoft.Json;
using Orleans.Configuration;
using Orleans.Serialization;
using Orleans.Storage;

var builder = WebApplication.CreateBuilder(args);

// builder.Configuration
//     .AddJsonFile("appsettings.json", false)
//     .AddEnvironmentVariables()
//     .AddCommandLine(args);
builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");
builder.AddRabbitMQClient("rmq");
builder.AddNpgsqlDbContext<ApplicationDbContext>("DefaultConnection");
builder.Services.AddControllers();
builder.Services.AddSwaggerGen().AddEndpointsApiExplorer();
builder.AddSelectiveForwardingUnit();
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
            // options.GrainStorageSerializer = new JsonGrainStorageSerializer(new OrleansJsonSerializer());
        })
        .UseLocalhostClustering();
});
var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();
var buildTime = File.GetLastWriteTimeUtc(typeof(Program).Assembly.Location);
app.MapGet("/", () => new { buildTime });
await app.WarpUp<ApplicationDbContext>().RunAsync();