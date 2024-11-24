namespace Argon.Api.Grains.Interfaces;

using Contracts;

[Alias("Argon.Api.Grains.Interfaces.IUserPreferenceGrain")]
public interface IUserPreferenceGrain : IGrainWithGuidKey, IUserPreferenceInteraction;