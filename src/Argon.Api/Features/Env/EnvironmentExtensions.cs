namespace Argon.Api.Features.Env;

using static File;

public static class EnvironmentExtensions
{
    public static bool IsKube(this IHostEnvironment env)
        => env.Determine() == ArgonEnvironmentKind.Kubernetes;

    public static bool IsDocker(this IHostEnvironment env)
        => env.Determine() == ArgonEnvironmentKind.Docker;

    public static bool IsClassicHost(this IHostEnvironment env)
        => env.Determine() == ArgonEnvironmentKind.HostMachine;

    public static bool IsManaged(this IHostEnvironment env)
        => env.IsDocker() || env.IsKube();

    public static ArgonEnvironmentKind Determine(this IHostEnvironment _)
    {
        if (Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST") != null)
            return ArgonEnvironmentKind.Kubernetes;
        if (Exists("/.dockerenv") || Directory.Exists("/proc/self/cgroup") &&
            ReadAllText("/proc/self/cgroup").Contains("docker"))
            return ArgonEnvironmentKind.Docker;
        return ArgonEnvironmentKind.HostMachine;
    }
}

public enum ArgonEnvironmentKind
{
    HostMachine,
    Docker,
    Kubernetes,
}