namespace Argon.Features.Jwt;

[GenerateSerializer, Alias("Argon.Api.Features.Jwt.TokenUserData")]
/// <param name="deviceId">
/// The machine this token was minted for, when it was minted from a refresh token bound to a
/// hardware key. Null for every unbound session, which is every session that has not enrolled a
/// device — those are judged on the hardware vector instead, if at all.
/// </param>
public record TokenUserData(Guid id, string machineId, Guid? deviceId = null);