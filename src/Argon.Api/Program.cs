using System.Data.Common;
using Argon.Api.Entities;
using Argon.Api.Migrations;
using Argon.Sfu;

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
        .AddAdoNetGrainStorage("OrleansStorage", options =>
        {
            options.Invariant = "Npgsql";
            options.ConnectionString = builder.Configuration.GetConnectionString("DefaultConnection");
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