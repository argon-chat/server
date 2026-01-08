namespace ArgonComplexTest.Tests;

using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

[TestFixture, Parallelizable(ParallelScope.None)]
public class SecurityTests : TestBase
{
    #region Email Change Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task RequestEmailChange_WithValidPassword_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newEmail = $"new_{Guid.NewGuid():N}@test.local";

        var result = await GetSecurityService(scope.ServiceProvider)
            .RequestEmailChange(newEmail, FakedTestCreds.password, ct);

        if (result is FailedRequestEmailChange failed)
            Assert.Fail($"Request failed with error: {failed.error}");

        Assert.That(result, Is.InstanceOf<SuccessRequestEmailChange>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task RequestEmailChange_WithInvalidPassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newEmail = $"new_{Guid.NewGuid():N}@test.local";

        var result = await GetSecurityService(scope.ServiceProvider)
            .RequestEmailChange(newEmail, "wrongpassword123", ct);

        Assert.That(result, Is.InstanceOf<FailedRequestEmailChange>());
        var failed = result as FailedRequestEmailChange;
        Assert.That(failed!.error, Is.EqualTo(EmailChangeError.INVALID_PASSWORD));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(2)]
    public async Task ConfirmEmailChange_WithValidCode_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newEmail = $"new_{Guid.NewGuid():N}@test.local";

        // Request email change
        var requestResult = await GetSecurityService(scope.ServiceProvider)
            .RequestEmailChange(newEmail, FakedTestCreds.password, ct);
        
        if (requestResult is FailedRequestEmailChange requestFailed)
            Assert.Fail($"Request failed with error: {requestFailed.error}");
        
        Assert.That(requestResult, Is.InstanceOf<SuccessRequestEmailChange>());

        // Get verification code from test store
        var code = await GetEmailCodeAsync(newEmail, ct: ct);
        Assert.That(code, Is.Not.Null, "Verification code should be available in test store");

        // Confirm email change
        var confirmResult = await GetSecurityService(scope.ServiceProvider)
            .ConfirmEmailChange(code!, ct);

        Assert.That(confirmResult, Is.InstanceOf<SuccessConfirmEmailChange>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(3)]
    public async Task ConfirmEmailChange_WithInvalidCode_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newEmail = $"new_{Guid.NewGuid():N}@test.local";

        // Request email change
        var requestResult = await GetSecurityService(scope.ServiceProvider)
            .RequestEmailChange(newEmail, FakedTestCreds.password, ct);
        
        if (requestResult is FailedRequestEmailChange requestFailed)
            Assert.Fail($"Request failed with error: {requestFailed.error}");

        // Try to confirm with wrong code
        var confirmResult = await GetSecurityService(scope.ServiceProvider)
            .ConfirmEmailChange("000000", ct);

        Assert.That(confirmResult, Is.InstanceOf<FailedConfirmEmailChange>());
        var failed = confirmResult as FailedConfirmEmailChange;
        Assert.That(failed!.error, Is.EqualTo(EmailChangeError.INVALID_VERIFICATION_CODE));
    }

    #endregion

    #region Password Change Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(10)]
    public async Task ChangePassword_WithValidCurrentPassword_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newPassword = "NewSecurePassword123!";

        var result = await GetSecurityService(scope.ServiceProvider)
            .ChangePassword(FakedTestCreds.password, newPassword, ct);

        if (result is FailedChangePassword failed)
            Assert.Fail($"Change password failed with error: {failed.error}");

        Assert.That(result, Is.InstanceOf<SuccessChangePassword>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(11)]
    public async Task ChangePassword_WithInvalidCurrentPassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .ChangePassword("wrongpassword123", "NewPassword123!", ct);

        Assert.That(result, Is.InstanceOf<FailedChangePassword>());
        var failed = result as FailedChangePassword;
        Assert.That(failed!.error, Is.EqualTo(PasswordChangeError.INVALID_CURRENT_PASSWORD));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(12)]
    public async Task ChangePassword_WithSamePassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .ChangePassword(FakedTestCreds.password, FakedTestCreds.password, ct);

        Assert.That(result, Is.InstanceOf<FailedChangePassword>());
        var failed = result as FailedChangePassword;
        Assert.That(failed!.error, Is.EqualTo(PasswordChangeError.PASSWORD_SAME_AS_CURRENT));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(13)]
    public async Task ChangePassword_WithShortPassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .ChangePassword(FakedTestCreds.password, "short", ct);

        Assert.That(result, Is.InstanceOf<FailedChangePassword>());
        var failed = result as FailedChangePassword;
        Assert.That(failed!.error, Is.EqualTo(PasswordChangeError.PASSWORD_TOO_SHORT));
    }

    #endregion

    #region OTP (TOTP) Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(20)]
    public async Task EnableOTP_WhenNotEnabled_ReturnsSecretAndQrCode(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider).EnableOTP(ct);

        Assert.That(result, Is.InstanceOf<SuccessEnableOTP>());
        var success = result as SuccessEnableOTP;
        Assert.That(success!.secret, Is.Not.Null.And.Not.Empty);
        Assert.That(success.qrCodeUrl, Does.Contain("otpauth://totp/"));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(21)]
    public async Task VerifyAndEnableOTP_WithInvalidCode_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // First enable OTP to get secret
        await GetSecurityService(scope.ServiceProvider).EnableOTP(ct);

        // Try to verify with invalid code
        var result = await GetSecurityService(scope.ServiceProvider)
            .VerifyAndEnableOTP("000000", ct);

        Assert.That(result, Is.InstanceOf<FailedVerifyOTP>());
        var failed = result as FailedVerifyOTP;
        Assert.That(failed!.error, Is.EqualTo(OTPError.INVALID_CODE));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(22)]
    public async Task DisableOTP_WhenNotEnabled_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider).DisableOTP("123456", ct);

        Assert.That(result, Is.InstanceOf<FailedDisableOTP>());
        var failed = result as FailedDisableOTP;
        Assert.That(failed!.error, Is.EqualTo(OTPError.NOT_ENABLED));
    }

    #endregion

    #region Passkey Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(30)]
    public async Task GetPasskeys_WhenEmpty_ReturnsEmptyList(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider).GetPasskeys(ct);

        Assert.That(result.Values, Is.Empty);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(31)]
    public async Task BeginAddPasskey_ReturnsChallenge(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .BeginAddPasskey("My Passkey", ct);

        Assert.That(result, Is.InstanceOf<SuccessBeginPasskey>());
        var success = result as SuccessBeginPasskey;
        Assert.That(success!.passkeyId, Is.Not.EqualTo(Guid.Empty));
        Assert.That(success.challenge, Is.Not.Null.And.Not.Empty);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(32)]
    public async Task CompleteAddPasskey_WithValidData_ReturnsPasskey(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // Begin add passkey
        var beginResult = await GetSecurityService(scope.ServiceProvider)
            .BeginAddPasskey("Test Passkey", ct);
        Assert.That(beginResult, Is.InstanceOf<SuccessBeginPasskey>());
        var begin = beginResult as SuccessBeginPasskey;

        // Complete with mock public key
        var mockPublicKey = Convert.ToBase64String(new byte[32]);
        var completeResult = await GetSecurityService(scope.ServiceProvider)
            .CompleteAddPasskey(begin!.passkeyId, mockPublicKey, ct);

        Assert.That(completeResult, Is.InstanceOf<SuccessCompletePasskey>());
        var complete = completeResult as SuccessCompletePasskey;
        Assert.That(complete!.passkey.name, Is.EqualTo("Test Passkey"));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(33)]
    public async Task RemovePasskey_WhenExists_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // Add passkey
        var beginResult = await GetSecurityService(scope.ServiceProvider)
            .BeginAddPasskey("To Remove", ct);
        var begin = beginResult as SuccessBeginPasskey;
        
        await GetSecurityService(scope.ServiceProvider)
            .CompleteAddPasskey(begin!.passkeyId, Convert.ToBase64String(new byte[32]), ct);

        // Remove passkey
        var removeResult = await GetSecurityService(scope.ServiceProvider)
            .RemovePasskey(begin.passkeyId, ct);

        Assert.That(removeResult, Is.InstanceOf<SuccessRemovePasskey>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(34)]
    public async Task RemovePasskey_WhenNotExists_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .RemovePasskey(Guid.NewGuid(), ct);

        Assert.That(result, Is.InstanceOf<FailedRemovePasskey>());
        var failed = result as FailedRemovePasskey;
        Assert.That(failed!.error, Is.EqualTo(PasskeyError.NOT_FOUND));
    }

    #endregion

    #region Auto Delete Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(40)]
    public async Task GetAutoDeletePeriod_WhenNotSet_ReturnsDefault12Months(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider).GetAutoDeletePeriod(ct);

        Assert.That(result.enabled, Is.True);
        Assert.That(result.months, Is.EqualTo(12));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(41)]
    public async Task SetAutoDeletePeriod_WithValidMonths_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .SetAutoDeletePeriod(24, ct);

        Assert.That(result, Is.InstanceOf<SuccessSetAutoDelete>());

        // Verify it was set
        var period = await GetSecurityService(scope.ServiceProvider).GetAutoDeletePeriod(ct);
        Assert.That(period.enabled, Is.True);
        Assert.That(period.months, Is.EqualTo(24));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(42)]
    public async Task SetAutoDeletePeriod_WithNull_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // Trying to disable auto-delete should fail
        var result = await GetSecurityService(scope.ServiceProvider)
            .SetAutoDeletePeriod(null, ct);

        Assert.That(result, Is.InstanceOf<FailedSetAutoDelete>());
        var failed = result as FailedSetAutoDelete;
        Assert.That(failed!.error, Is.EqualTo(AutoDeleteError.INVALID_PERIOD));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(43)]
    public async Task SetAutoDeletePeriod_WithInvalidMonths_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // Try invalid period (> 36 months)
        var result = await GetSecurityService(scope.ServiceProvider)
            .SetAutoDeletePeriod(100, ct);

        Assert.That(result, Is.InstanceOf<FailedSetAutoDelete>());
        var failed = result as FailedSetAutoDelete;
        Assert.That(failed!.error, Is.EqualTo(AutoDeleteError.INVALID_PERIOD));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(44)]
    public async Task SetAutoDeletePeriod_WithZeroMonths_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // Try invalid period (0 months)
        var result = await GetSecurityService(scope.ServiceProvider)
            .SetAutoDeletePeriod(0, ct);

        Assert.That(result, Is.InstanceOf<FailedSetAutoDelete>());
        var failed = result as FailedSetAutoDelete;
        Assert.That(failed!.error, Is.EqualTo(AutoDeleteError.INVALID_PERIOD));
    }

    #endregion

    #region Phone Change Tests

    [Test, CancelAfter(1000 * 60 * 5), Order(50)]
    public async Task RequestPhoneChange_WithValidPassword_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newPhone = "+79001234567";

        var result = await GetSecurityService(scope.ServiceProvider)
            .RequestPhoneChange(newPhone, FakedTestCreds.password, ct);

        if (result is FailedRequestPhoneChange failed)
            Assert.Fail($"Request failed with error: {failed.error}");

        Assert.That(result, Is.InstanceOf<SuccessRequestPhoneChange>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(51)]
    public async Task RequestPhoneChange_WithInvalidPassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .RequestPhoneChange("+79001234567", "wrongpassword", ct);

        Assert.That(result, Is.InstanceOf<FailedRequestPhoneChange>());
        var failed = result as FailedRequestPhoneChange;
        Assert.That(failed!.error, Is.EqualTo(PhoneChangeError.INVALID_PASSWORD));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(52)]
    public async Task ConfirmPhoneChange_WithValidCode_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var newPhone = "+79009876543";
        var normalizedPhone = "+79009876543"; // Same after normalization

        // Request phone change
        var requestResult = await GetSecurityService(scope.ServiceProvider)
            .RequestPhoneChange(newPhone, FakedTestCreds.password, ct);
        
        if (requestResult is FailedRequestPhoneChange requestFailed)
            Assert.Fail($"Request failed with error: {requestFailed.error}");
        
        Assert.That(requestResult, Is.InstanceOf<SuccessRequestPhoneChange>());

        // Get verification code from test store (NullPhoneChannel stores it with normalized phone)
        var code = await GetPhoneCodeAsync(normalizedPhone, ct: ct);
        Assert.That(code, Is.Not.Null, "Phone verification code should be available in test store");

        // Confirm phone change
        var confirmResult = await GetSecurityService(scope.ServiceProvider)
            .ConfirmPhoneChange(code!, ct);

        Assert.That(confirmResult, Is.InstanceOf<SuccessConfirmPhoneChange>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(53)]
    public async Task RemovePhone_WithValidPassword_ReturnsSuccess(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .RemovePhone(FakedTestCreds.password, ct);

        if (result is FailedRemovePhone failed)
            Assert.Fail($"Remove phone failed with error: {failed.error}");

        Assert.That(result, Is.InstanceOf<SuccessRemovePhone>());
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(54)]
    public async Task RemovePhone_WithInvalidPassword_ReturnsFailed(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var result = await GetSecurityService(scope.ServiceProvider)
            .RemovePhone("wrongpassword", ct);

        Assert.That(result, Is.InstanceOf<FailedRemovePhone>());
        var failed = result as FailedRemovePhone;
        Assert.That(failed!.error, Is.EqualTo(PhoneChangeError.INVALID_PASSWORD));
    }

    #endregion
}
