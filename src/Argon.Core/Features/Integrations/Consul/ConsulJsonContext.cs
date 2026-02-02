namespace Argon.Api.Features.Orleans.Consul;

using System.Text.Json;
using System.Text.Json.Serialization;

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    IncludeFields = true)]
[JsonSerializable(typeof(ConsulOrleansTableVersion))]
[JsonSerializable(typeof(MembershipEntry))]
[JsonSerializable(typeof(SiloAddress))]
[JsonSerializable(typeof(List<Tuple<SiloAddress, DateTime>>))]
[JsonSerializable(typeof(GrainAddress))]
internal partial class ConsulJsonContext : JsonSerializerContext
{
}
