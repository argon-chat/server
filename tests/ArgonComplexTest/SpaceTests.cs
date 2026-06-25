namespace ArgonComplexTest.Tests;

using ArgonContracts;
using Microsoft.EntityFrameworkCore;
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
            expireMinutes: 60,
            maxUses: 10,
            ct);

        Assert.That(inviteCode.inviteCode, Is.Not.Null.And.Not.Empty);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(3)]
    public async Task PreviewInvite_WithValidCode_ReturnsPreview(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var createSpaceResult = await GetUserService(scope.ServiceProvider).CreateSpace(
            new CreateServerRequest("Preview Space", "Preview Description", string.Empty),
            ct);
        var successSpace = createSpaceResult as SuccessCreateSpace;
        Assert.That(successSpace, Is.Not.Null);

        var invite = await GetServerService(scope.ServiceProvider).CreateInviteCode(
            successSpace!.space.spaceId,
            expireMinutes: 60,
            maxUses: 10,
            ct);

        var previewResult = await GetUserService(scope.ServiceProvider).PreviewInvite(invite, ct);

        Assert.That(previewResult, Is.Not.Null, "PreviewInvite must never return null");
        Assert.That(previewResult, Is.InstanceOf<SuccessPreview>());

        var preview = (previewResult as SuccessPreview)!.preview;
        Assert.That(preview.spaceId, Is.EqualTo(successSpace.space.spaceId));
        Assert.That(preview.name, Is.EqualTo("Preview Space"));
        Assert.That(preview.description, Is.EqualTo("Preview Description"));
        Assert.That(preview.memberCount, Is.GreaterThanOrEqualTo(1), "Owner counts as a member");
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(3)]
    public async Task PreviewInvite_WithSharedDashedCode_ReturnsPreview(CancellationToken ct = default)
    {
        // Repro of the prod bug: GetInviteCodes (what the client shares) returns the code
        // in its dashed form (DecodeFromUlong → "ABC-DEF-GHI", 11 chars), but PreviewInvite
        // used to reject any length != 9/12, so every shared invite previewed as null.
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var createSpaceResult = await GetUserService(scope.ServiceProvider).CreateSpace(
            new CreateServerRequest("Shared Space", "Shared Description", string.Empty),
            ct);
        var successSpace = createSpaceResult as SuccessCreateSpace;
        Assert.That(successSpace, Is.Not.Null);

        var invite = await GetServerService(scope.ServiceProvider).CreateInviteCode(
            successSpace!.space.spaceId,
            expireMinutes: 60,
            maxUses: 10,
            ct);

        // The exact transform GetInviteCodes applies to what the client shares.
        var sharedCode = Argon.Entities.InviteCodeEntityData.DecodeFromUlong(
            Argon.Entities.InviteCodeEntityData.EncodeToUlong(invite.inviteCode));
        Assert.That(sharedCode, Does.Contain("-"), "GetInviteCodes hands out the dashed form");

        var previewResult = await GetUserService(scope.ServiceProvider)
            .PreviewInvite(new InviteCode(sharedCode), ct);

        Assert.That(previewResult, Is.InstanceOf<SuccessPreview>(),
            "Previewing a shared (dashed) invite code must resolve the space");
        Assert.That((previewResult as SuccessPreview)!.preview.spaceId,
            Is.EqualTo(successSpace.space.spaceId));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(3)]
    public async Task JoinToSpace_WithSharedDashedCode_Succeeds(CancellationToken ct = default)
    {
        // The actual "invites broke" path: joining through the dashed code the client shares.
        await using var scope1 = FactoryAsp.Services.CreateAsyncScope();
        await using var scope2 = FactoryAsp.Services.CreateAsyncScope();

        var ownerToken = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(ownerToken);

        var createSpaceResult = await GetUserService(scope1.ServiceProvider).CreateSpace(
            new CreateServerRequest("Join Space", "Join Description", string.Empty),
            ct);
        var spaceId = (createSpaceResult as SuccessCreateSpace)!.space.spaceId;

        var invite = await GetServerService(scope1.ServiceProvider).CreateInviteCode(
            spaceId, expireMinutes: 60, maxUses: 10, ct);
        var sharedCode = Argon.Entities.InviteCodeEntityData.DecodeFromUlong(
            Argon.Entities.InviteCodeEntityData.EncodeToUlong(invite.inviteCode));

        // Second user joins via the shared (dashed) code.
        Setup();
        var joinerToken = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(joinerToken);

        var joinResult = await GetUserService(scope2.ServiceProvider)
            .JoinToSpace(new InviteCode(sharedCode), ct);

        Assert.That(joinResult, Is.InstanceOf<SuccessJoin>(),
            "Joining with a shared (dashed) invite code must succeed");
        Assert.That((joinResult as SuccessJoin)!.space.spaceId, Is.EqualTo(spaceId));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(4)]
    public async Task PreviewInvite_WithUnknownCode_ReturnsNotFound(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        // Well-formed invite code that was never persisted.
        var unknownCode = new InviteCode(Argon.Entities.InviteCodeEntityData.GenerateInviteCode());

        var previewResult = await GetUserService(scope.ServiceProvider).PreviewInvite(unknownCode, ct);

        Assert.That(previewResult, Is.Not.Null, "PreviewInvite must never return null");
        Assert.That(previewResult, Is.InstanceOf<FailedPreview>());
        Assert.That((previewResult as FailedPreview)!.error, Is.EqualTo(AcceptInviteError.NOT_FOUND));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(5)]
    public async Task PreviewInvite_WithExpiredCode_ReturnsExpired(CancellationToken ct = default)
    {
        await using var scope = FactoryAsp.Services.CreateAsyncScope();

        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        var me = await GetUserService(scope.ServiceProvider).GetMe(ct);
        var createSpaceResult = await GetUserService(scope.ServiceProvider).CreateSpace(
            new CreateServerRequest("Expired Space", "desc", string.Empty),
            ct);
        var spaceId = (createSpaceResult as SuccessCreateSpace)!.space.spaceId;

        // The API only creates non-expired invites, so seed an already-expired one directly.
        var code = await CreateRawInviteAsync(spaceId, me.userId, DateTimeOffset.UtcNow.AddMinutes(-5), ct);

        var previewResult = await GetUserService(scope.ServiceProvider).PreviewInvite(new InviteCode(code), ct);

        Assert.That(previewResult, Is.Not.Null, "PreviewInvite must never return null");
        Assert.That(previewResult, Is.InstanceOf<FailedPreview>());
        Assert.That((previewResult as FailedPreview)!.error, Is.EqualTo(AcceptInviteError.EXPIRED));
    }

    private async Task<string> CreateRawInviteAsync(Guid spaceId, Guid creatorId, DateTimeOffset expireAt, CancellationToken ct)
    {
        var factory = FactoryAsp.Services.GetRequiredService<IDbContextFactory<Argon.Entities.ApplicationDbContext>>();
        await using var db = await factory.CreateDbContextAsync(ct);

        var code = Argon.Entities.InviteCodeEntityData.GenerateInviteCode();
        db.Invites.Add(new Argon.Entities.SpaceInvite
        {
            Id        = Argon.Entities.InviteCodeEntityData.EncodeToUlong(code),
            SpaceId   = spaceId,
            CreatorId = creatorId,
            ExpireAt  = expireAt,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);

        return code;
    }
}
