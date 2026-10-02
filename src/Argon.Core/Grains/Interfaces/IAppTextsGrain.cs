namespace Argon.Grains.Interfaces;

using ArgonContracts;

/// <summary>Applications' localized strings as readers see them. Stateless — key it with <see cref="Guid.Empty"/>.</summary>
[Alias($"Argon.Grains.Interfaces.{nameof(IAppTextsGrain)}")]
public interface IAppTextsGrain : IGrainWithGuidKey
{
    /// <summary>Every key in every locale; null when there is no such application.</summary>
    [Alias(nameof(GetAsync))]
    Task<AppTexts?> GetAsync(IAppRef app);
}
