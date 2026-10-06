namespace Argon.Grains.Interfaces;

/// <summary>
/// Step-up verification for one account: proof that whoever holds this session is the account holder
/// right now, before a sensitive action. Keyed by user id.
/// </summary>
/// <remarks>
/// A flow is bound to the action it was begun for and to the session that began it. The action itself
/// lives with whoever owns its data (<see cref="ISecurityGrain"/> for the email change): it asks here
/// whether the flow is verified, and may prove a new contact with <see cref="SendTargetCodeAsync"/> and
/// <see cref="CheckTargetCodeAsync"/> before calling <see cref="CompleteAsync"/>.
/// </remarks>
[Alias("Argon.Grains.Interfaces.IVerificationGrain")]
public interface IVerificationGrain : IGrainWithGuidKey
{
    [Alias(nameof(BeginAsync))]
    Task<IBeginVerificationResult> BeginAsync(SensitiveAction action, Guid sessionId, CancellationToken ct = default);

    [Alias(nameof(ChallengeAsync))]
    Task<IChallengeVerificationResult> ChallengeAsync(Guid flowId, VerificationFactor factor, Guid sessionId, CancellationToken ct = default);

    [Alias(nameof(SubmitAsync))]
    Task<ISubmitVerificationResult> SubmitAsync(Guid flowId, VerificationFactor factor, string proof, Guid sessionId, CancellationToken ct = default);

    [Alias(nameof(CancelAsync))]
    Task CancelAsync(Guid flowId, Guid sessionId, CancellationToken ct = default);

    [Alias(nameof(IsVerifiedAsync))]
    Task<bool> IsVerifiedAsync(Guid flowId, SensitiveAction action, Guid sessionId, CancellationToken ct = default);

    /// <summary>Sends a code to a contact the action is about to switch to. A repeat to the same contact waits out the cooldown.</summary>
    [Alias(nameof(SendTargetCodeAsync))]
    Task<TargetCodeSend> SendTargetCodeAsync(Guid flowId, SensitiveAction action, Guid sessionId, string target, CancellationToken ct = default);

    /// <summary>A wrong code spends an attempt of the flow, like any other wrong answer in it.</summary>
    [Alias(nameof(CheckTargetCodeAsync))]
    Task<TargetCodeCheck> CheckTargetCodeAsync(Guid flowId, SensitiveAction action, Guid sessionId, string code, CancellationToken ct = default);

    [Alias(nameof(CompleteAsync))]
    Task CompleteAsync(Guid flowId, CancellationToken ct = default);
}

public enum TargetCodeOutcome
{
    Sent,
    NotVerified,
    TooSoon,
    RateLimited
}

[GenerateSerializer, Immutable]
public sealed record TargetCodeSend
{
    [Id(0)] public required TargetCodeOutcome Outcome { get; init; }
    [Id(1)] public DateTimeOffset? ResendAt { get; init; }
}

public enum TargetCheckOutcome
{
    Verified,
    NotVerified,
    NoCode,
    Invalid,
    // The wrong code was the flow's last attempt; the flow is gone.
    Exhausted
}

[GenerateSerializer, Immutable]
public sealed record TargetCodeCheck
{
    [Id(0)] public required TargetCheckOutcome Outcome { get; init; }
    [Id(1)] public string? Target { get; init; }
}
