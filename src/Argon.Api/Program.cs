using Argon.Api.Common.Models;
using Argon.Api.Common.Services;
using Argon.Api.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Orleans.Clustering.Kubernetes;
using Orleans.Configuration;

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
        builder.Services.AddAuthorization();
        builder.Services
            .AddIdentityApiEndpoints<ApplicationUser>(options =>
            {
                options.SignIn.RequireConfirmedAccount = true;
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
                options.SignIn.RequireConfirmedEmail = true;
                options.User.RequireUniqueEmail = true;
            })
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddDefaultTokenProviders();
        builder.Services.AddTransient<IEmailSender<ApplicationUser>, EmailSender>();
        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();
        builder.Host.UseOrleans(siloBuilder =>
        {
            siloBuilder
                .Configure<ClusterOptions>(options =>
                {
                    options.ClusterId = "argon";
                    options.ServiceId = "argon-api";
                }).Configure<ConnectionOptions>(connection =>
                {
                    connection.OpenConnectionTimeout = TimeSpan.FromSeconds(30);
                })
                .ConfigureEndpoints(11111, 30000, listenOnAnyHostAddress: true)
                .AddMemoryGrainStorageAsDefault();

            if (builder.Environment.IsProduction()) siloBuilder.UseKubeMembership();
            else siloBuilder.UseLocalhostClustering();
        });
        builder.Services.AddCors(options =>
        {
            options.AddDefaultPolicy(
                policy => { policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin(); });
        });
        var app = builder.Build();
        app.UseCors();
        app.UseSwagger();
        app.UseSwaggerUI();
        if (app.Environment.IsDevelopment())
        {
            using var scope = app.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Thread.Sleep(5000);
            await db.Database.MigrateAsync();
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();
        app.MapDefaultEndpoints();
        var buildTime = File.GetLastWriteTimeUtc(typeof(Program).Assembly.Location);
        app.MapGet("/", () => new { buildTime });
        app.MapGroup("/api/identity").MapIdentityApi<ApplicationUser>().RequireCors();
        await app.RunAsync();
    }
}