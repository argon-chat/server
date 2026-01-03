namespace ArgonComplexTest.Tests;

using ArgonContracts;
using Microsoft.Extensions.DependencyInjection;

[TestFixture, Parallelizable(ParallelScope.None)]
public class SpaceTests : TestBase
{
    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task CreateSpace_WithValidData_ReturnsSpaceId(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var createSpaceResult = await GetUserService(scope.ServiceProvider).CreateSpace(
            new CreateServerRequest("Test Space", "Test Description", string.Empty),
            ct);

        if (createSpaceResult is FailedCreateSpace failed)
        {
            Assert.Fail($"Failed to create space: {failed.error}");
            return;
        }

        Assert.That(createSpaceResult, Is.InstanceOf<SuccessCreateSpace>());
        var success = createSpaceResult as SuccessCreateSpace;
        Assert.That(success!.space.spaceId, Is.Not.EqualTo(Guid.Empty));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task GetSpaces_AfterCreation_ReturnsOneSpace(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var createResult = await GetUserService(scope.ServiceProvider).CreateSpace(
            new CreateServerRequest("Test Space", "Description", string.Empty),
            ct);
        
        if (createResult is not SuccessCreateSpace)
        {
            var failed = createResult as FailedCreateSpace;
            Assert.Fail($"Failed to create space: {failed!.error}");
            return;
        }

        var spaces = await GetUserService(scope.ServiceProvider).GetSpaces(ct);

        Assert.That(spaces.Values.Count, Is.EqualTo(1), $"Expected exactly 1 space, but got {spaces.Values.Count}");
        Assert.That(spaces.Values[0].name, Is.EqualTo("Test Space"));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(2)]
    public async Task CreateInviteCode_ForSpace_ReturnsValidCode(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var createSpaceResult = await GetUserService(scope.ServiceProvider).CreateSpace(
            new CreateServerRequest("Test Space", "Test Description", string.Empty),
            ct);

        var successSpace = createSpaceResult as SuccessCreateSpace;
        Assert.That(successSpace, Is.Not.Null);

        var inviteCode = await GetServerService(scope.ServiceProvider).CreateInviteCode(
            successSpace!.space.spaceId,
            ct);

        Assert.That(inviteCode.inviteCode, Is.Not.Null.And.Not.Empty);
    }
}
