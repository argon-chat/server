namespace Argon.Api.Features.Jwt;

[GenerateSerializer, Alias("Argon.Api.Features.Jwt.TokenUserData")]
public record TokenUserData(Guid id, Guid machineId);