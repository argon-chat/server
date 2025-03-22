namespace Argon.Api.Features.Orleans.Consul;

public interface IArgonUnitMembership : IMembershipTable
{
    public const string ArgonServiceName = "Argon Unit";
    public const string ArgonNameSpace   = "argon-unit";
    public const string WorkerUnit       = "compute-unit";
    public const string GatewayUnit      = "edge-unit";
    public const string LoopBackHealth   = "LoopBackHealth";
}