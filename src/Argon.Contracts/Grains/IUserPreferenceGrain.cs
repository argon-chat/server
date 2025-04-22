namespace Argon.Grains.Interfaces;

[Alias("Argon.Grains.Interfaces.IUserPreferenceGrain")]
public interface IUserPreferenceGrain : IGrainWithGuidKey, IUserPreferenceInteraction;