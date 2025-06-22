namespace Argon.Features.Env;

using System.Diagnostics;
using Extensions;

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


    public static bool IsMultiRegion(this WebApplicationBuilder env)
        => env.Environment.IsMultiRegion();
    public static bool IsSingleRegion(this WebApplicationBuilder env)
        => env.Environment.IsSingleRegion();
    public static bool IsSingleInstance(this WebApplicationBuilder env)
        => env.Environment.IsSingleInstance();
    public static bool IsRegionalMode(this WebApplicationBuilder env)
        => env.Environment.IsRegionalMode();

    public static ArgonEnvironmentKind Determine(this IHostEnvironment _)
    {
        //if (Debugger.IsAttached)
        //    return ArgonEnvironmentKind.SingleInstance;

        if (Environment.GetEnvironmentVariable("ARGON_MODE") is { } newEnv)
            return Enum.Parse<ArgonEnvironmentKind>(newEnv);
        if (Environment.GetCommandLineArgs().Contains("migrations") ||
            Environment.GetCommandLineArgs().Contains("database"))
            return ArgonEnvironmentKind.SingleRegion;
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

    public static bool IsUseLocalHostCerts(this WebApplicationBuilder _)
        => Environment.GetEnvironmentVariable("USE_LOCALHOST_CERTS") is not null;

    public static void SetDatacenter(this WebApplicationBuilder builder, string dc)
        => builder.Host.Properties.Add("dc", dc);
    public static string GetDatacenter(this WebApplicationBuilder builder)
        => builder.Host.Properties["dc"].As<object, string>();

    public static void SetFeatureOptions(this WebApplicationBuilder builder, FeaturesOptions options)
        => builder.Host.Properties.Add("features", options);

    public static FeaturesOptions GetFeatureOptions(this WebApplicationBuilder builder)
        => builder.Host.Properties["features"].As<object, FeaturesOptions>();

    public static string DetermineClientSpace(this IHostEnvironment env)
    {
        if (env.IsEntryPoint())
            return "EntryPoint";
        if (env.IsGateway())
            return "Gateway";
        if (env.IsWorker())
            return "Worker";
        if (env.IsHybrid() && (env.IsMultiRegion() || env.IsSingleRegion()))
            throw new InvalidOperationException($"Multi Regional or Single Regional unit cannot be assign to Hybrid role!");
        if (env.IsHybrid())
            return "EntryPoint";
        throw new InvalidOperationException("Cannot determine consul client role");
    }


    public static ArgonRoleKind DetermineRole(this IHostEnvironment _)
    {
        if (Environment.GetEnvironmentVariable("ARGON_ROLE") is { } newEnv)
            return Enum.Parse<ArgonRoleKind>(newEnv);

        if (Environment.GetCommandLineArgs().Contains("migrations") ||
            Environment.GetCommandLineArgs().Contains("database"))
            return ArgonRoleKind.Worker;

        throw new InvalidOperationException($"No defined 'ARGON_ROLE' environment variable, no defined argon role");
    }
}

public enum ArgonRoleKind
{
    Hybrid,
    Gateway,
    EntryPoint,
    Worker
}