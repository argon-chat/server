namespace ArgonComplexTest.Tests;

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Argon.Entities;
using Argon.Grains.Interfaces;
using ArgonContracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

[TestFixture, Parallelizable(ParallelScope.None)]
public class XsollaWebHookTests : TestBase
{
    [SetUp]
    public void ResetFakeXsolla() => GetFakeXsolla().Reset();

    private async Task<HttpResponseMessage> PostWebhook(object payload)
    {
        var json = JsonSerializer.Serialize(payload);
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/xsolla/webhook")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Authorization", "Signature test-signature");
        return await HttpClient.SendAsync(request);
    }

    #region User Validation

    [Test, CancelAfter(1000 * 60 * 5), Order(0)]
    public async Task Webhook_UserValidation_ExistingUser_Returns204(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var response = await PostWebhook(new
        {
            notification_type = "user_validation",
            user = new { id = user.userId.ToString() }
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(1)]
    public async Task Webhook_UserValidation_UnknownUser_Returns404(CancellationToken ct = default)
    {
        var response = await PostWebhook(new
        {
            notification_type = "user_validation",
            user = new { id = Guid.NewGuid().ToString() }
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound));
    }

    #endregion

    #region Payments

    [Test, CancelAfter(1000 * 60 * 5), Order(10)]
    public async Task Webhook_Payment_Subscription_ActivatesSub(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var response = await PostWebhook(new
        {
            notification_type = "payment",
            transaction = new { id = 12345L },
            purchase = new
            {
                subscription = new
                {
                    plan_id = "ultima_monthly",
                    subscription_id = 9999L
                }
            },
            custom_parameters = new
            {
                type = "subscription",
                user_id = user.userId.ToString()
            }
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        // Verify subscription activated
        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        var sub = await grain.GetSubscriptionAsync(ct);
        Assert.That(sub, Is.Not.Null);
        Assert.That(sub!.tier, Is.EqualTo(UltimaPlan.Monthly));
        Assert.That(sub.status, Is.EqualTo(UltimaSubscriptionStatus.Active));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(11)]
    public async Task Webhook_Payment_BoostPack_GrantsBoosts(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var response = await PostWebhook(new
        {
            notification_type = "payment",
            transaction = new { id = 12346L },
            custom_parameters = new
            {
                type = "boost_pack",
                user_id = user.userId.ToString(),
                pack_type = "Pack3",
                boost_count = 3
            }
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        var boosts = await grain.GetBoostsAsync(ct);
        Assert.That(boosts, Has.Count.EqualTo(3));
        Assert.That(boosts.All(b => b.source == BoostSource.PurchasedPack3), Is.True);
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(12)]
    public async Task Webhook_Payment_Gift_CreatesItemAndGrantsSenderBoosts(CancellationToken ct = default)
    {
        // Register sender
        var senderToken = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(senderToken);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var sender = await GetUserService(scope.ServiceProvider).GetMe(ct);

        // Register recipient
        var recipientToken = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(recipientToken);
        var recipient = await GetUserService(scope.ServiceProvider).GetMe(ct);

        var response = await PostWebhook(new
        {
            notification_type = "payment",
            transaction = new { id = 12347L },
            custom_parameters = new
            {
                type = "gift",
                user_id = sender.userId.ToString(),
                recipient_id = recipient.userId.ToString(),
                plan = "Monthly",
                gift_message = "Happy birthday!"
            }
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        // Verify recipient got inventory item
        SetAuthToken(recipientToken);
        var items = await GetInventoryService(scope.ServiceProvider).GetMyInventoryItems(ct);
        Assert.That(items.Values.Any(i => i.id == "ultima_gift"), Is.True);

        // Verify sender got 3 GiftReward boosts
        var senderGrain = GetGrainFactory().GetGrain<IUltimaGrain>(sender.userId);
        var senderBoosts = await senderGrain.GetBoostsAsync(ct);
        Assert.That(senderBoosts.Count(b => b.source == BoostSource.GiftReward), Is.EqualTo(3));
    }

    #endregion

    #region Cancel & Refund

    [Test, CancelAfter(1000 * 60 * 5), Order(20)]
    public async Task Webhook_CancelSubscription_CancelsSubscription(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        // Activate first
        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, "sub_to_cancel", null, ct);

        // Send cancel webhook
        var response = await PostWebhook(new
        {
            notification_type = "cancel_subscription",
            user = new { id = user.userId.ToString() }
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        var sub = await grain.GetSubscriptionAsync(ct);
        Assert.That(sub, Is.Not.Null);
        Assert.That(sub!.status, Is.EqualTo(UltimaSubscriptionStatus.Cancelled));
    }

    [Test, CancelAfter(1000 * 60 * 5), Order(21)]
    public async Task Webhook_Refund_ExpiresSubscription(CancellationToken ct = default)
    {
        var token = await RegisterAndGetTokenAsync(ct);
        SetAuthToken(token);

        await using var scope = FactoryAsp.Services.CreateAsyncScope();
        var user = await GetUserService(scope.ServiceProvider).GetMe(ct);

        // Activate first
        var grain = GetGrainFactory().GetGrain<IUltimaGrain>(user.userId);
        await grain.ActivateSubscriptionAsync(UltimaTier.Monthly, 30, "sub_to_refund", null, ct);

        // Verify boosts exist
        var boostsBefore = await grain.GetBoostsAsync(ct);
        Assert.That(boostsBefore, Has.Count.EqualTo(3));

        // Send refund webhook
        var response = await PostWebhook(new
        {
            notification_type = "refund",
            custom_parameters = new
            {
                user_id = user.userId.ToString()
            }
        });

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));

        // Subscription expired, boosts removed
        var boostsAfter = await grain.GetBoostsAsync(ct);
        Assert.That(boostsAfter, Has.Count.EqualTo(0));

        var ctx = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await ctx.CreateDbContextAsync(ct);
        var userEntity = await db.Users.AsNoTracking().FirstAsync(x => x.Id == user.userId, ct);
        Assert.That(userEntity.HasActiveUltima, Is.False);
    }

    #endregion
}
