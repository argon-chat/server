namespace ArgonComplexTest.Tests;

using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

[TestFixture, Parallelizable(ParallelScope.None)]
public class IdentityTests : TestBase
{
    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task GetAuthorizationScenario_ReturnsEmailOtp(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var result = await GetIdentityService(scope.ServiceProvider).GetAuthorizationScenario(ct);
        Assert.That(result, Is.EqualTo("Email_Otp"));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task Registration_WithValidData_ReturnsTokens(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        Assert.That(token, Is.Not.Null.And.Not.Empty);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(2)]
    public async Task Authorization_WithValidCredentials_ReturnsTokens(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        
        await RegisterAndGetTokenAsync(ct);

        var authResult = await GetIdentityService(scope.ServiceProvider).Authorize(
            new UserCredentialsInput(FakedTestCreds.email, null, null, FakedTestCreds.password, null, null),
            ct);

        Assert.That(authResult, Is.InstanceOf<SuccessAuthorize>());
        var success = authResult as SuccessAuthorize;
        Assert.That(success!.token, Is.Not.Null.And.Not.Empty);
        Assert.That(success.refreshToken, Is.Not.Null.And.Not.Empty);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(3)]
    public async Task BeginResetPassword_WithRegisteredEmail_ReturnsTrue(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        
        await RegisterAndGetTokenAsync(ct);

        var result = await GetIdentityService(scope.ServiceProvider).BeginResetPassword(
            FakedTestCreds.email,
            ct);

        Assert.That(result, Is.True);
    }
}
