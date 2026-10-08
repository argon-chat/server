using Argon.Features.Aegis;

if (BotApiCli.TryHandleCommand(args))
    return;

var validation = ArgonClusterCli.ValidationOptionsFor(ArgonProfile.Orleans);

if (ArgonClusterCli.TryHandleCommand(args, validation) is { } clusterExitCode)
{
    Environment.ExitCode = clusterExitCode;
    return;
}

var builder = WebApplication.CreateBuilder(args);

var role = builder.AddArgonRole(args);

builder.ValidateArgonTopologyOnBoot(args, validation);
builder.AddArgonOrleans(role, ArgonProfile.Orleans);

var app = builder.Build();

app.UseArgonRole();

await app.WarmUp<ApplicationDbContext>();
await app.WarmUpKeyRing();

await app.RunAsync();
