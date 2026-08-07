using ArgonComplexTest.Infrastructure;

/// <summary>
/// Assembly-wide setup. Deliberately in the global namespace: NUnit applies a
/// <see cref="SetUpFixtureAttribute"/> to its own namespace and everything below it, and the global
/// namespace is the only one that covers every fixture in the assembly regardless of where it lives.
/// </summary>
[SetUpFixture]
public class GlobalTestSetup
{
    [OneTimeSetUp]
    public Task StartInfrastructure() => ArgonTestEnvironment.StartAsync();

    [OneTimeTearDown]
    public async Task StopInfrastructure()
    {
        if (ArgonTestEnvironment.IsInitialised)
            await ArgonTestEnvironment.Instance.DisposeAsync();
    }
}
