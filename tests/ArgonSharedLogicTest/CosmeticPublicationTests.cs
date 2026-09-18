namespace ArgonSharedLogicTest;

using Argon.Entities;
using Argon.Features.Cosmetics;
using Argon.Features.Cosmetics.Kinds;

/// <summary>
/// The gate between an operator's draft and every client that would render it.
/// </summary>
/// <remarks>
/// <para>Asserted as separate refusals rather than one "publish fails", so that loosening any single
/// condition has to be done deliberately.</para>
///
/// <para>Rows are built by hand rather than through the console, because the console is one caller
/// and the rule is not its property.</para>
/// </remarks>
[TestFixture]
public class CosmeticPublicationTests
{
    private static CosmeticKindDefinition Background()
        => CosmeticKindRegistry.Describe(typeof(ProfileBackgroundKind));

    private static CosmeticKindDefinition NicknameStyle()
        => CosmeticKindRegistry.Describe(typeof(NicknameStyleKind));

    private static CosmeticKindDefinition FontOption()
        => CosmeticKindRegistry.Describe(typeof(FontOptionKind));

    /// <summary>The locales a complete row has been named in.</summary>
    private static readonly HashSet<string> Named = new(StringComparer.Ordinal) { "en" };

    private static readonly HashSet<string> Nameless = new(StringComparer.Ordinal);

    /// <summary>A row that would publish, which each test then spoils in exactly one way.</summary>
    private static CosmeticItemEntity Publishable() => new()
    {
        Id           = Guid.NewGuid(),
        KindKey      = "profile.background",
        Slug         = "sakura",
        NameKey      = "cosmetic_background_sakura",
        Payload      = """{"loop":true,"tintOpacity":0.3}""",
        AssetFileIds = new Dictionary<string, string> { ["Primary"] = Guid.NewGuid().ToString() },
        AssetSource  = CosmeticAssetSource.Catalogue
    };

    [Test]
    public void A_complete_row_publishes()
    {
        var verdict = CosmeticPublication.Evaluate(Publishable(), Background(), Named);

        Assert.That(verdict.IsAllowed, Is.True, verdict.Detail);
    }

    [Test]
    public void A_row_whose_kind_is_gone_does_not_publish()
    {
        var verdict = CosmeticPublication.Evaluate(Publishable(), null, Named);

        Assert.That(verdict.Refusal, Is.EqualTo(CosmeticPublicationRefusal.UnknownKind));
    }

    [Test]
    public void Publishing_twice_is_refused()
    {
        var item = Publishable();
        item.IsPublished = true;

        Assert.That(CosmeticPublication.Evaluate(item, Background(), Named).Refusal,
            Is.EqualTo(CosmeticPublicationRefusal.AlreadyPublished));
    }

    [Test]
    public void A_payload_its_kind_rejects_does_not_publish()
    {
        var item = Publishable();
        item.Payload = """{"tintOpacity":42}""";

        Assert.That(CosmeticPublication.Evaluate(item, Background(), Named).Refusal,
            Is.EqualTo(CosmeticPublicationRefusal.PayloadInvalid));
    }

    [Test]
    public void A_missing_required_asset_does_not_publish()
    {
        var item = Publishable();
        item.AssetFileIds = new Dictionary<string, string>();

        var verdict = CosmeticPublication.Evaluate(item, Background(), Named);

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Refusal, Is.EqualTo(CosmeticPublicationRefusal.MissingAsset));
            Assert.That(verdict.Detail, Does.Contain("Primary"));
        });
    }

    /// <summary>
    /// An item whose asset the wearer supplies has nothing for the catalogue to carry.
    /// </summary>
    [Test]
    public void A_user_provided_item_needs_no_catalogue_asset()
    {
        var item = Publishable();
        item.AssetFileIds = new Dictionary<string, string>();
        item.AssetSource  = CosmeticAssetSource.UserProvided;

        var verdict = CosmeticPublication.Evaluate(item, Background(), Named);

        Assert.That(verdict.IsAllowed, Is.True, verdict.Detail);
    }

    /// <summary>
    /// A font is declared with <c>AllowsAsset</c>, not <c>RequiresAsset</c>: a face already in the
    /// bundle is named rather than uploaded, so a row carrying no file is still publishable.
    /// </summary>
    [Test]
    public void A_font_option_with_no_file_publishes()
    {
        var item = new CosmeticItemEntity
        {
            Id           = Guid.NewGuid(),
            KindKey      = "option.font",
            Slug         = "gilded",
            NameKey      = "cosmetic_font_gilded",
            Payload      = """{"CssFamily":"Gilded, serif"}""",
            AssetFileIds = new Dictionary<string, string>(),
            AssetSource  = CosmeticAssetSource.Catalogue
        };

        var verdict = CosmeticPublication.Evaluate(item, FontOption(), Named);

        Assert.That(verdict.IsAllowed, Is.True, verdict.Detail);
    }

    /// <summary>
    /// A published row is read by clients that have no key file to fall back on, so the one
    /// language every client can read is the floor.
    /// </summary>
    [Test]
    public void A_row_with_no_name_does_not_publish()
    {
        var verdict = CosmeticPublication.Evaluate(Publishable(), Background(), Nameless);

        Assert.Multiple(() =>
        {
            Assert.That(verdict.Refusal, Is.EqualTo(CosmeticPublicationRefusal.MissingName));
            Assert.That(verdict.Detail, Does.Contain("en"));
        });
    }

    [Test]
    public void A_row_named_only_in_another_language_does_not_publish()
    {
        var verdict = CosmeticPublication.Evaluate(Publishable(), Background(),
            new HashSet<string>(StringComparer.Ordinal) { "am" });

        Assert.That(verdict.Refusal, Is.EqualTo(CosmeticPublicationRefusal.MissingName));
    }
}
