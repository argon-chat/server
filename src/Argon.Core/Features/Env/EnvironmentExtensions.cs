namespace Argon.Features.Env;

public static class EnvironmentExtensions
{
    public static bool IsSingleInstance(this IHostEnvironment env)
        => env.Determine() == ArgonEnvironmentKind.SingleInstance;

    public static bool IsSingleInstance(this WebApplicationBuilder env)
        => env.Environment.IsSingleInstance();

    public static ArgonEnvironmentKind Determine(this IHostEnvironment _)
    {
        if (Environment.GetEnvironmentVariable("ARGON_MODE") is { } newEnv)
            return Enum.Parse<ArgonEnvironmentKind>(newEnv);
        if (Environment.GetCommandLineArgs().Contains("migrations") ||
            Environment.GetCommandLineArgs().Contains("database"))
            return ArgonEnvironmentKind.Production;
        throw new InvalidOperationException("No defined 'ARGON_MODE' environment variable, no defined argon mode");
    }
}

public enum ArgonEnvironmentKind
{
    SingleInstance,
    Production
}


public static class EnvironmentRoleExtensions
{
    extension(IHostEnvironment env)
    {
        public bool IsGateway()
            => env.DetermineRole() == ArgonRoleKind.Gateway;

        public bool IsEntryPoint()
            => env.DetermineRole() == ArgonRoleKind.EntryPoint;

        public bool IsWorker()
            => env.DetermineRole() == ArgonRoleKind.Worker;

        public bool IsHybrid()
            => env.DetermineRole() == ArgonRoleKind.Hybrid;
    }

    extension(WebApplicationBuilder env)
    {
        public bool IsGatewayRole()
            => env.Environment.DetermineRole() == ArgonRoleKind.Gateway;

        public bool IsEntryPointRole()
            => env.Environment.DetermineRole() == ArgonRoleKind.EntryPoint;

        public bool IsWorkerRole()
            => env.Environment.DetermineRole() == ArgonRoleKind.Worker;

        public bool IsHybridRole()
            => env.Environment.DetermineRole() == ArgonRoleKind.Hybrid;

        public bool IsUseLocalHostCerts()
            => Environment.GetEnvironmentVariable("USE_LOCALHOST_CERTS") is not null;
    }

    public static string DetermineClientSpace(this IHostEnvironment env)
    {
        if (env.IsEntryPoint())
            return "EntryPoint";
        if (env.IsGateway())
            return "Gateway";
        if (env.IsWorker())
            return "Worker";
        if (env.IsHybrid())
            return "EntryPoint";
        throw new InvalidOperationException("Cannot determine client space for unknown role");
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