namespace Argon.Features.Env;

using System.Diagnostics;

public static class EnvironmentExtensions
{
    public static bool IsMultiRegion(this IHostEnvironment env)
        => env.Determine() == ArgonEnvironmentKind.MultiRegion;
    public static bool IsSingleRegion(this IHostEnvironment env)
        => env.Determine() == ArgonEnvironmentKind.SingleRegion;
    public static bool IsSingleInstance(this IHostEnvironment env)
        => env.Determine() == ArgonEnvironmentKind.SingleInstance;

    public static bool IsRegionalMode(this IHostEnvironment env)
        => env.IsSingleInstance() || env.IsMultiRegion();

    public static ArgonEnvironmentKind Determine(this IHostEnvironment _)
    {
        if (Debugger.IsAttached)
            return ArgonEnvironmentKind.SingleInstance;

        if (Environment.GetEnvironmentVariable("ARGON_MODE") is { } newEnv)
            return Enum.Parse<ArgonEnvironmentKind>(newEnv);
        throw new InvalidOperationException("No defined 'ARGON_MODE' environment variable, no defined argon mode");
    }
}

public enum ArgonEnvironmentKind
{
    SingleInstance,
    SingleRegion,
    MultiRegion
}


public static class EnvironmentRoleExtensions
{
    public static bool IsGateway(this IHostEnvironment env)
        => env.DetermineRole() == ArgonRoleKind.Gateway;
    public static bool IsEntryPoint(this IHostEnvironment env)
        => env.DetermineRole() == ArgonRoleKind.EntryPoint;
    public static bool IsWorker(this IHostEnvironment env)
        => env.DetermineRole() == ArgonRoleKind.Worker;
    public static bool IsHybrid(this IHostEnvironment env)
        => env.DetermineRole() == ArgonRoleKind.Hybrid;

    public static bool IsGatewayRole(this WebApplicationBuilder env)
        => env.Environment.DetermineRole() == ArgonRoleKind.Gateway;
    public static bool IsEntryPointRole(this WebApplicationBuilder env)
        => env.Environment.DetermineRole() == ArgonRoleKind.EntryPoint;
    public static bool IsWorkerRole(this WebApplicationBuilder env)
        => env.Environment.DetermineRole() == ArgonRoleKind.Worker;
    public static bool IsHybridRole(this WebApplicationBuilder env)
        => env.Environment.DetermineRole() == ArgonRoleKind.Hybrid;

    public static ArgonRoleKind DetermineRole(this IHostEnvironment _)
    {
        if (Environment.GetEnvironmentVariable("ARGON_ROLE") is { } newEnv)
            return Enum.Parse<ArgonRoleKind>(newEnv);
        throw new InvalidOperationException("No defined 'ARGON_ROLE' environment variable, no defined argon role");
    }
}

public enum ArgonRoleKind
{
    Hybrid,
    Gateway,
    EntryPoint,
    Worker
}