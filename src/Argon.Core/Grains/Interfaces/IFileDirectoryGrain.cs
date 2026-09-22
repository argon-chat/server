namespace Argon.Grains.Interfaces;

/// <summary>
/// Read-only lookups about stored files, for roles that address files without holding a database.
/// </summary>
/// <remarks>
/// Stateless, keyed by <see cref="Guid.Empty"/>. The file's owner-scoped operations are
/// <see cref="Argon.Api.Grains.Interfaces.IFileStorageGrain"/>'s; this only answers where a file is.
/// </remarks>
[Alias("Argon.Grains.Interfaces.IFileDirectoryGrain")]
public interface IFileDirectoryGrain : IGrainWithGuidKey
{
    /// <summary>The object key of a finalized file, or null when there is no such file yet.</summary>
    [Alias(nameof(GetObjectKeyAsync))]
    Task<string?> GetObjectKeyAsync(Guid fileId, CancellationToken ct = default);
}
