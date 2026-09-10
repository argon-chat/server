namespace ArgonSharedLogicTest;

using Argon.Features.BotApi;

/// <summary>
/// The bot contract verifier is the guard rail on the event payloads bots receive over SSE: it
/// hashes each payload type and compares that against the hash pinned in source. If it stops
/// working, a breaking change to a published event ships silently.
/// <para>
/// It is also the mechanism behind <c>dotnet run -- bot-api verify</c> in CI, so these tests double
/// as a check that the CI gate itself still functions.
/// </para>
/// </summary>
[TestFixture]
public class BotContractVerifierTests
{
    [Test]
    public void Verify_FindsNoMismatchesInTheCurrentTree()
        // The same assertion the CI contract gate makes. Failing here means a published event
        // payload changed shape without its pinned hash being updated.
        => Assert.That(BotContractVerifier.Verify(), Is.Empty,
            "a pinned bot contract hash no longer matches the code it describes");

    // The HTTP surface used to be asserted here, over [BotRoute] attributes that restated the
    // routes. It lives in BotOpenApiTests now, over the endpoints that are actually mapped.

    [Test]
    public void ComputeEventContractHash_IsDeterministic()
    {
        var first  = BotContractVerifier.ComputeEventContractHash(typeof(ReadyEventPayload));
        var second = BotContractVerifier.ComputeEventContractHash(typeof(ReadyEventPayload));

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.EqualTo(second));
            Assert.That(first, Has.Length.EqualTo(64));
        });
    }

    [Test]
    public void ComputeEventContractHash_DiffersBetweenDifferentPayloads()
        => Assert.That(
            BotContractVerifier.ComputeEventContractHash(typeof(ReadyEventPayload)),
            Is.Not.EqualTo(BotContractVerifier.ComputeEventContractHash(typeof(HeartbeatEventPayload))));

    [Test]
    public void DiscoverEventDefinitions_FindsTheLifecycleEvents()
    {
        var events = BotContractVerifier.DiscoverEventDefinitions();

        Assert.Multiple(() =>
        {
            Assert.That(events, Is.Not.Empty);
            Assert.That(events.Select(e => e.Type), Does.Contain(typeof(ReadyEventPayload)));
            Assert.That(events.Select(e => e.Type), Does.Contain(typeof(HeartbeatEventPayload)));
        });
    }

    [Test]
    public void DiscoverDtoTypes_FindsTheVersionedBotDtos()
    {
        var dtos = BotContractVerifier.DiscoverDtoTypes();

        Assert.Multiple(() =>
        {
            Assert.That(dtos, Is.Not.Empty);
            Assert.That(dtos.Select(d => d.Type), Does.Contain(typeof(BotControlV1)));
            Assert.That(dtos.All(d => d.Version > 0), Is.True);
        });
    }

    [Test]
    public void GenerateDocsManifest_CoversIntentsEventsRateLimitsAndDtos()
    {
        var docs = BotContractVerifier.GenerateDocsManifest();

        Assert.Multiple(() =>
        {
            Assert.That(docs.Intents, Is.Not.Empty);
            Assert.That(docs.Events, Is.Not.Empty);
            Assert.That(docs.RateLimits, Is.Not.Empty);
            Assert.That(docs.Dtos, Is.Not.Empty);
        });
    }

    [Test]
    public void GenerateDocsManifest_IntentsExcludeTheAggregateAliases()
    {
        // None / AllNonPrivileged / AllPrivileged are convenience masks, not intents a bot can
        // request individually; publishing them as such would mislead integrators.
        var docs = BotContractVerifier.GenerateDocsManifest();

        Assert.That(docs.Intents.Select(i => i.Name),
            Has.None.EqualTo(nameof(BotIntent.None))
              .And.None.EqualTo(nameof(BotIntent.AllNonPrivileged))
              .And.None.EqualTo(nameof(BotIntent.AllPrivileged)));
    }

    [Test]
    public void GenerateDocsManifest_EachIntentBitMatchesItsValue()
    {
        var docs = BotContractVerifier.GenerateDocsManifest();

        foreach (var intent in docs.Intents)
            Assert.That(1L << intent.Bit, Is.EqualTo(intent.Value), $"intent {intent.Name}");
    }

    [Test]
    public void DiffDtoVersions_OfATypeAgainstItself_IsEmpty()
        => Assert.That(
            BotContractVerifier.DiffDtoVersions(typeof(BotControlV1), typeof(BotControlV1)),
            Is.Empty);

    [Test]
    public void DiffDtoVersions_ReportsAddedAndRemovedFields()
    {
        var changes = BotContractVerifier.DiffDtoVersions(typeof(OldShape), typeof(NewShape));

        Assert.Multiple(() =>
        {
            Assert.That(changes.Select(c => c.FieldName), Does.Contain(nameof(NewShape.Added)));
            Assert.That(changes.Select(c => c.FieldName), Does.Contain(nameof(OldShape.Removed)));
        });
    }

    [Test]
    public void DiffDtoVersions_ReportsARetypedField()
    {
        var changes = BotContractVerifier.DiffDtoVersions(typeof(OldShape), typeof(RetypedShape));

        Assert.That(changes.Select(c => c.FieldName), Does.Contain(nameof(OldShape.Kept)));
    }

    private sealed record OldShape(string Kept, int Removed);

    private sealed record NewShape(string Kept, bool Added);

    private sealed record RetypedShape(int Kept, int Removed);
}
