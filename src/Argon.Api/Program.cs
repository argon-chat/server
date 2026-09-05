using Argon.Features.Aegis;

if (BotApiCli.TryHandleCommand(args))
    return;

if (ArgonClusterCli.TryHandleCommand(args, ArgonClusterCli.DefaultValidationOptions) is { } clusterExitCode)
{
    Environment.ExitCode = clusterExitCode;
    return;
}

var builder = WebApplication.CreateBuilder(args);

var role = builder.AddArgonRole(args);

builder.ValidateArgonTopologyOnBoot(args, ArgonClusterCli.DefaultValidationOptions);
builder.AddArgonOrleans(role);

var app = builder.Build();

app.UseArgonRole();

await app.WarmUp<ApplicationDbContext>();
await app.WarmUpKeyRing();

await app.RunAsync();
