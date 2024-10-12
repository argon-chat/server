using Argon.Api.Entities;
using Microsoft.EntityFrameworkCore;

namespace Argon.Api;

public class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddServiceDefaults();
        builder.AddRedisOutputCache("cache");
        builder.AddRabbitMQClient("rmq");
        builder.AddNpgsqlDbContext<ApplicationDbContext>("DefaultConnection");
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Host.UseOrleans(static siloBuilder =>
        {
            siloBuilder.UseLocalhostClustering();
            siloBuilder.AddMemoryGrainStorage("replaceme"); // TODO: replace me pls
        });
        var app = builder.Build();
        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Thread.Sleep(5000);
            await db.Database.MigrateAsync();
        }

        app.UseHttpsRedirection();
        app.UseAuthorization();
        app.MapControllers();
        app.MapDefaultEndpoints();
        await app.RunAsync();
    }
}