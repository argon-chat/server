namespace ArgonSharedLogicTest;

using Argon.Features.Cosmetics;
using Argon.Features.Cosmetics.Kinds;
using ArgonContracts;

/// <summary>
/// A worn scene's reach: what the wearer's choice is stored as, and what the stored choice goes out as.
/// </summary>
/// <remarks>
/// The two directions have to agree, because a profile carries back exactly what was put on. A choice
/// that cannot be stored stores nothing, and a stored nothing goes out as a plain worn item that defers
/// to the row's own default.
/// </remarks>
[TestFixture]
public class ProfileSceneKindTests
{
    private static readonly Guid Scene = Guid.NewGuid();

    [TestCase(SceneReach.Content, "content")]
    [TestCase(SceneReach.Card, "card")]
    public void A_chosen_reach_is_stored_as_its_word(SceneReach reach, string word)
        => Assert.That(ProfileSceneKind.TuningOf(reach).Reach, Is.EqualTo(word));

    /// <summary>The wire's enum is open: a reach a newer client knows and this build does not is no word at all.</summary>
    [Test]
    public void A_reach_this_build_has_no_word_for_is_stored_as_nothing()
        => Assert.That(ProfileSceneKind.TuningOf((SceneReach)9).Reach, Is.Null);

    [TestCase("content", SceneReach.Content)]
    [TestCase("card", SceneReach.Card)]
    public void A_stored_reach_goes_out_as_a_worn_scene(string word, SceneReach reach)
        => Assert.That(ProfileSceneKind.ToWire(Scene, new ProfileSceneTuning { Reach = word }), Is.EqualTo(new WornScene(Scene, reach)));

    [TestCase(null)]
    [TestCase("far")]
    public void A_stored_tuning_that_names_no_reach_goes_out_as_a_plain_item(string? word)
        => Assert.That(ProfileSceneKind.ToWire(Scene, new ProfileSceneTuning { Reach = word }), Is.EqualTo(new WornItem(Scene)));

    [Test]
    public void No_stored_tuning_goes_out_as_a_plain_item()
        => Assert.That(ProfileSceneKind.ToWire(Scene, null), Is.EqualTo(new WornItem(Scene)));

    [TestCase(SceneReach.Content)]
    [TestCase(SceneReach.Card)]
    public void A_choice_survives_being_stored_and_sent(SceneReach reach)
        => Assert.That(ProfileSceneKind.ToWire(Scene, ProfileSceneKind.TuningOf(reach)), Is.EqualTo(new WornScene(Scene, reach)));
}
