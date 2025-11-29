namespace Argon.Core.Grains.Interfaces;

[Alias(nameof(IBillingGrain))]
public interface IBillingGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetWalletDetailsAsync))]
    Task<ArgonWalletDetails> GetWalletDetailsAsync(Guid userId, CancellationToken ct = default);


    [Alias(nameof(DepositAsync))]
    Task DepositAsync(Guid userId, long amount, string comment, string refId, CancellationToken ct = default);
}

public record ArgonWalletDetails(Guid userId, long balance, long hold, string lastTrxId);