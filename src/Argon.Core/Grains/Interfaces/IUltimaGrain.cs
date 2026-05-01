namespace Argon.Grains.Interfaces;

[Alias(nameof(IUltimaGrain))]
public interface IUltimaGrain : IGrainWithGuidKey
{
    [Alias(nameof(GetSubscriptionAsync))]
    Task<UltimaSubscriptionInfo?> GetSubscriptionAsync(CancellationToken ct = default);

    [Alias(nameof(GetXsollaSubscriptionIdAsync))]
    Task<string?> GetXsollaSubscriptionIdAsync(CancellationToken ct = default);

    [Alias(nameof(GetTransactionHistoryAsync))]
    Task<List<UltimaTransaction>> GetTransactionHistoryAsync(CancellationToken ct = default);

    [Alias(nameof(GetBoostsAsync))]
    Task<List<UltimaBoost>> GetBoostsAsync(CancellationToken ct = default);

    [Alias(nameof(ApplyBoostAsync))]
    Task<IApplyBoostResult> ApplyBoostAsync(Guid boostId, Guid spaceId, CancellationToken ct = default);

    [Alias(nameof(TransferBoostAsync))]
    Task<ITransferBoostResult> TransferBoostAsync(Guid boostId, Guid newSpaceId, CancellationToken ct = default);

    [Alias(nameof(RemoveBoostAsync))]
    Task<bool> RemoveBoostAsync(Guid boostId, CancellationToken ct = default);

    [Alias(nameof(ActivateSubscriptionAsync))]
    Task ActivateSubscriptionAsync(UltimaTier tier, int durationDays, string? xsollaSubId, Guid? fromItemId, CancellationToken ct = default);

    [Alias(nameof(ExpireSubscriptionAsync))]
    Task ExpireSubscriptionAsync(CancellationToken ct = default);

    [Alias(nameof(CancelSubscriptionAsync))]
    Task<bool> CancelSubscriptionAsync(CancellationToken ct = default);

    [Alias(nameof(GrantPurchasedBoostsAsync))]
    Task GrantPurchasedBoostsAsync(int count, BoostSource source, string? xsollaTxId, CancellationToken ct = default);

    [Alias(nameof(SaveTransactionAsync))]
    Task SaveTransactionAsync(string txId, string transactionType, string? planExternalId, string? boostPackType,
        int? boostCount, Guid? recipientId, string? amount, string? currency,
        string? cardSuffix = null, string? cardBrand = null, long? paymentAccountId = null, CancellationToken ct = default);

    [Alias(nameof(MarkTransactionRefundedAsync))]
    Task MarkTransactionRefundedAsync(string txId, CancellationToken ct = default);
}
