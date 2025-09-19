namespace Argon.Features.Jwt;

[GenerateSerializer, Alias("Argon.Api.Features.Jwt.TokenUserData")]
public record TokenUserData(Guid id, string machineId);