namespace ArgonComplexTest.Tests;

using System.Net.Http.Json;
using System.Text.Json;
using Argon.Entities;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[TestFixture]
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

    private sealed record Resolved(string Kind, string? InstanceUrl);

    /// <summary>
    /// The anonymous domain lookup sends a verified domain to its instance and everyone else to the
    /// official one, and keeps saying so once the answer is cached.
    /// </summary>
    [Test, CancelAfter(1000 * 60 * 2), Order(10)]
    public async Task DiscoveryResolve_RoutesOnlyAVerifiedDomainToItsInstance(CancellationToken ct = default)
    {
        var managed    = $"{Guid.NewGuid():N}.example";
        var unverified = $"{Guid.NewGuid():N}.example";

        await using (var db = await FactoryAsp.Services
                        .GetRequiredService<IDbContextFactory<ApplicationDbContext>>()
                        .CreateDbContextAsync(ct))
        {
            db.TenantDirectory.Add(new TenantDirectoryEntity
            {
                Id = Guid.CreateVersion7(), Domain = managed, InstanceUrl = "https://acme.example", IsVerified = true
            });
            db.TenantDirectory.Add(new TenantDirectoryEntity
            {
                Id = Guid.CreateVersion7(), Domain = unverified, InstanceUrl = "https://claimed.example", IsVerified = false
            });
            await db.SaveChangesAsync(ct);
        }

        async Task<Resolved?> Resolve(string email)
            => await HttpClient.GetFromJsonAsync<Resolved>(
                $"/api/discovery/resolve?email={Uri.EscapeDataString(email)}",
                new JsonSerializerOptions(JsonSerializerDefaults.Web), ct);

        var first      = await Resolve($"alice@{managed}");
        var cached     = await Resolve($"bob@{managed.ToUpperInvariant()}");
        var notYetOurs = await Resolve($"mallory@{unverified}");
        var nobody     = await Resolve($"carol@{Guid.NewGuid():N}.example");

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(new Resolved("managed", "https://acme.example")));
            Assert.That(cached, Is.EqualTo(first), "the cached answer disagrees with the first one");
            Assert.That(notYetOurs, Is.EqualTo(new Resolved("official", null)),
                "an unverified claim on a domain routed its sign-ins elsewhere");
            Assert.That(nobody, Is.EqualTo(new Resolved("official", null)));
        });
    }
}
