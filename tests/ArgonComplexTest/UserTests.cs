namespace ArgonComplexTest.Tests;

using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

[TestFixture, Parallelizable(ParallelScope.None)]
public class UserTests : TestBase
{
    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task GetMyProfile_AfterRegistration_ReturnsProfile(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var profile = await GetUserService(scope.ServiceProvider).GetMyProfile(ct);

        Assert.That(profile, Is.Not.Null);
        Assert.That(profile.userId, Is.Not.EqualTo(Guid.Empty));
    }
}
