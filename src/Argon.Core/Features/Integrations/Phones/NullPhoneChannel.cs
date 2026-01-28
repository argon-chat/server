namespace Argon.Features.Integrations.Phones;

using System.Collections.Concurrent;
using Testing;

/// <summary>
/// Null/Mock phone channel for testing and local development.
/// Stores codes in memory and logs them.
/// </summary>
public class NullPhoneChannel(ILogger<NullPhoneChannel> logger, ITestCodeStore? testCodeStore = null) : IPhoneChannel
{
    private readonly ConcurrentDictionary<string, StoredCode> _codes = new();

    public PhoneChannelKind Kind => PhoneChannelKind.Null;
    public bool IsEnabled => true;

    public Task<bool> CanSendAsync(string phoneNumber, CancellationToken ct = default)
        => Task.FromResult(true);

    public Task<PhoneSendResult> SendCodeAsync(PhoneSendRequest request, CancellationToken ct = default)
    {
        var code = GenerateCode(request.CodeLength);
        var requestId = Guid.NewGuid().ToString("N");

        var stored = new StoredCode(
            Code: code,
            PhoneNumber: request.PhoneNumber,
            ExpiresAt: DateTime.UtcNow.AddMinutes(10),
            AttemptsLeft: 5);

        _codes[requestId] = stored;

        // Store code for test extraction
        testCodeStore?.StoreCode(request.PhoneNumber, code, TestCodeType.Phone);

        logger.LogWarning(
            "[NULL PHONE CHANNEL] Verification code for {PhoneNumber}: {Code} (RequestId: {RequestId})",
            request.PhoneNumber, code, requestId);

        return Task.FromResult(new PhoneSendResult(
            Success: true,
            RequestId: requestId,
            UsedChannel: PhoneChannelKind.Null));
    }

    public Task<PhoneVerifyResult> VerifyCodeAsync(PhoneVerifyRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(request.RequestId))
        {
            // Try to find by phone number (for channels that don't use request IDs)
            var entry = _codes.FirstOrDefault(x => x.Value.PhoneNumber == request.PhoneNumber);
            if (entry.Key is null)
                return Task.FromResult(new PhoneVerifyResult(PhoneVerifyStatus.NotFound));

            return VerifyWithKey(entry.Key, request.Code);
        }

        return VerifyWithKey(request.RequestId, request.Code);
    }

    private Task<PhoneVerifyResult> VerifyWithKey(string requestId, string code)
    {
        if (!_codes.TryGetValue(requestId, out var stored))
            return Task.FromResult(new PhoneVerifyResult(PhoneVerifyStatus.NotFound));

        if (stored.ExpiresAt < DateTime.UtcNow)
        {
            _codes.TryRemove(requestId, out _);
            return Task.FromResult(new PhoneVerifyResult(PhoneVerifyStatus.Expired));
        }

        if (stored.AttemptsLeft <= 0)
        {
            _codes.TryRemove(requestId, out _);
            return Task.FromResult(new PhoneVerifyResult(PhoneVerifyStatus.TooManyAttempts));
        }

        if (stored.Code == code)
        {
            _codes.TryRemove(requestId, out _);
            logger.LogInformation("[NULL PHONE CHANNEL] Code verified successfully for RequestId: {RequestId}", requestId);
            return Task.FromResult(new PhoneVerifyResult(PhoneVerifyStatus.Verified));
        }

        // Decrement attempts
        var updated = stored with { AttemptsLeft = stored.AttemptsLeft - 1 };
        _codes[requestId] = updated;

        logger.LogWarning(
            "[NULL PHONE CHANNEL] Invalid code for RequestId: {RequestId}. Attempts left: {AttemptsLeft}",
            requestId, updated.AttemptsLeft);

        return Task.FromResult(new PhoneVerifyResult(
            PhoneVerifyStatus.InvalidCode,
            RemainingAttempts: updated.AttemptsLeft));
    }

    private static string GenerateCode(int length)
    {
        // Use cryptographically secure random even for test/dev environments
        // to ensure proper security practices throughout the codebase
        using var rng = RandomNumberGenerator.Create();
        var randomBytes = new byte[length];
        rng.GetBytes(randomBytes);
        
        var code = new char[length];
        for (var i = 0; i < length; i++)
        {
            // Convert to digit 0-9
            code[i] = (char)('0' + (randomBytes[i] % 10));
        }
        return new string(code);
    }

    private record StoredCode(string Code, string PhoneNumber, DateTime ExpiresAt, int AttemptsLeft);
}
