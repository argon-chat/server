namespace ArgonSharedLogicTest;

using Argon.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

/// <summary>
/// What every dependency check gets from its base: a bound on how long it may take, and an answer
/// rather than an exception whatever happens underneath.
/// </summary>
[TestFixture]
public class DependencyHealthCheckTests
{
    private sealed class Probe(Func<CancellationToken, Task<HealthCheckResult>> body, TimeSpan timeout)
        : DependencyHealthCheck(Options.Create(new ProbeOptions { Dependencies = { Timeout = timeout } }))
    {
        protected override Task<HealthCheckResult> ProbeAsync(CancellationToken ct) => body(ct);
    }

    private static Task<HealthCheckResult> Run(Func<CancellationToken, Task<HealthCheckResult>> body)
    {
        var check = new Probe(body, TimeSpan.FromMilliseconds(200));

        return check.CheckHealthAsync(new HealthCheckContext
        {
            Registration = new HealthCheckRegistration(DependencyNames.Nats, check, null, null)
        });
    }

    /// <summary>
    /// The clients these checks go through were configured to wait — NATS gets a minute to connect
    /// — and a probe that inherits the wait is answered after Kubernetes has stopped listening.
    /// </summary>
    [Test]
    public async Task A_check_that_honours_the_token_is_cut_off_at_the_timeout()
    {
        var result = await Run(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return HealthCheckResult.Healthy();
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(HealthStatus.Unhealthy));
            Assert.That(result.Description, Does.Contain("nats did not answer within"));
            Assert.That(result.Data, Does.ContainKey("timeout"));
        });
    }

    /// <summary>LiveKit's client takes no token at all. The bound has to hold without one.</summary>
    [Test]
    public async Task A_check_that_ignores_the_token_is_cut_off_all_the_same()
    {
        var result = await Run(async _ =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None);
            return HealthCheckResult.Healthy();
        });

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(HealthStatus.Unhealthy));
            Assert.That(result.Description, Does.Contain("did not answer within"));
        });
    }

    [Test]
    public async Task A_check_that_throws_answers_with_the_message()
    {
        var result = await Run(_ => throw new InvalidOperationException("connection refused to 10.4.2.19:4222"));

        Assert.Multiple(() =>
        {
            Assert.That(result.Status, Is.EqualTo(HealthStatus.Unhealthy));
            Assert.That(result.Description, Is.EqualTo("nats: connection refused to 10.4.2.19:4222"));
            Assert.That(result.Exception, Is.Not.Null, "the exception rides along for /health");
        });
    }

    [Test]
    public async Task An_answer_passes_through_untouched()
    {
        var data = new Dictionary<string, object> { ["rttMs"] = 1.5 };

        var healthy = await Run(_ => Task.FromResult(HealthCheckResult.Healthy("answered", data)));
        var sealedVault = await Run(_ => Task.FromResult(HealthCheckResult.Unhealthy("Vault is sealed")));

        Assert.Multiple(() =>
        {
            Assert.That(healthy.Status, Is.EqualTo(HealthStatus.Healthy));
            Assert.That(healthy.Description, Is.EqualTo("answered"));
            Assert.That(healthy.Data["rttMs"], Is.EqualTo(1.5));

            Assert.That(sealedVault.Status, Is.EqualTo(HealthStatus.Unhealthy));
            Assert.That(sealedVault.Description, Is.EqualTo("Vault is sealed"),
                "a check that phrased its own failure is not rephrased");
        });
    }
}
