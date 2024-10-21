using System.Text;
using Argon.Api.Entities;
using Argon.Api.Extensions;
using Argon.Api.Migrations;
using Argon.Sfu;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Orleans.Configuration;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.AddRedisOutputCache("cache");
builder.AddRabbitMQClient("rmq");
builder.AddNpgsqlDbContext<ApplicationDbContext>("DefaultConnection");
builder.Services.AddControllers();
builder.Services.AddSwaggerGen(c =>
    {
        // c.SwaggerDoc("v1", new Microsoft.OpenApi.Models.OpenApiInfo { Title = "Global Header Example", Version = "v1" });
        c.OperationFilter<AuthorizationHeaderParameterOperationFilter>();
    })
    .AddEndpointsApiExplorer();
builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
}).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidIssuer = builder.Configuration["Jwt:Issuer"],
        ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"])),
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = false,
        ValidateIssuerSigningKey = true
    };

    o.Events = new JwtBearerEvents
    {
        OnMessageReceived = ctx =>
        {
            if (ctx.Request.Headers.TryGetValue("x-argon-token", out var value))
            {
                ctx.Token = value;
                return Task.CompletedTask;
            }

            if (ctx.Request.Cookies.TryGetValue("x-argon-token", out var cookie))
            {
                ctx.Token = cookie;
                return Task.CompletedTask;
            }

            ctx.Response.StatusCode = 401;
            return Task.CompletedTask;
        }
    };
});
builder.Services.AddAuthorizationCore();
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
        })
        .UseLocalhostClustering();
});
var app = builder.Build();
app.UseSwagger();
app.UseSwaggerUI();
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapDefaultEndpoints();
var buildTime = File.GetLastWriteTimeUtc(typeof(Program).Assembly.Location);
app.MapGet("/", () => new { buildTime });
await app.WarpUp<ApplicationDbContext>().RunAsync();