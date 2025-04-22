namespace Argon.Api.Features.Orleans.Consul;

public interface IArgonUnitMembership : IMembershipTable
{
    public const string ArgonServiceName      = "Argon Unit";
    public const string EntryPointServiceName = "Argon Entry Point";
    public const string ArgonNameSpace        = "argon-unit";
    public const string WorkerUnit            = "compute-unit";
    public const string GatewayUnit           = "edge-unit";
    public const string EntryUnit             = "edge-unit";
    public const string EnterpriceEditionUnit = "ee-unit";
    public const string LoopBackHealth        = "LoopBackHealth";
}