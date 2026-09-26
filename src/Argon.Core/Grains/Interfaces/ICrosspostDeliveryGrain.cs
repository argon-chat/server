namespace Argon.Grains.Interfaces;

/// <summary>
/// Delivers one published post to the followers of its channel, keyed by the source channel id with
/// the message id as the extension. Progress is persisted page by page and a reminder resumes it after
/// a deactivation or a silo restart.
/// </summary>
[Alias("Argon.Grains.Interfaces.ICrosspostDeliveryGrain")]
public interface ICrosspostDeliveryGrain : IGrainWithGuidCompoundKey
{
    /// <summary>Takes the job and starts it once this call has returned; a repeat does nothing.</summary>
    [Alias(nameof(StartAsync))]
    Task StartAsync(Guid sourceSpaceId);
}
